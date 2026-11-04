import path from "node:path";
import { fileURLToPath } from "node:url";
import { build, defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// The OSDU Delivery GUI is SQLFlow's GUI with the OSDU Delivery module installed. SQLFlow's sources are compiled in place
// from the vendored tree, never copied: `@/` resolves into sqlflow/gui/src for both trees (so SQLFlow's own `@/` imports
// keep working), `@osdu/` into this module's src, and every package either tree imports resolves from this build's
// node_modules. That gives one copy of React, the router and the query client at runtime, whether or not sqlflow/gui
// has an install of its own.
//
// Like SQLFlow's GUI this is a separate-origin SPA: the API base URL comes from public/config.json at runtime
// (VITE_API_BASE_URL wins in the dev server), and port 5173 matches the control plane's development CORS allowlist.

const guiRoot = fileURLToPath(new URL(".", import.meta.url));
const moduleSrc = path.resolve(guiRoot, "src");
const sqlflowGui = path.resolve(guiRoot, "../../sqlflow/gui");
const sqlflowSrc = path.resolve(sqlflowGui, "src");
// The key census files the YAML editor analyses the module's documents with. They sit beside the documentation they are
// written from, and the suites check them against the loaders there.
const censusDir = path.resolve(guiRoot, "../docs/census");

const forwardSlashes = (value: string) => value.replace(/\\/g, "/");

function isInside(file: string, directory: string): boolean {
  const relative = path.relative(directory, file);
  return relative !== "" && !relative.startsWith("..") && !path.isAbsolute(relative);
}

/** A package import ("react", "@tanstack/react-query/x"), as opposed to a path, an alias, or a virtual module. */
function isPackageImport(source: string): boolean {
  return !(
    source.startsWith(".")
    || source.startsWith("/")
    || source.startsWith("\0")
    || source.startsWith("@/")
    || source.startsWith("@osdu/")
    || source.includes(":")
  );
}

/**
 * Resolves the packages SQLFlow's sources import as if this build's entry imported them. Without it, node resolution
 * from sqlflow/gui/src would look for sqlflow/gui/node_modules: failing when that folder does not exist, and bundling a
 * second React when it does.
 */
function vendoredPackagesFromThisBuild(): Plugin {
  const anchor = path.join(guiRoot, "index.html");
  return {
    name: "osdu-gui:vendored-packages",
    enforce: "pre",
    async resolveId(source, importer, options) {
      if (importer === undefined || !isPackageImport(source)) {
        return null;
      }

      const importerFile = path.resolve(importer.split("?")[0]);
      if (!isInside(importerFile, sqlflowSrc)) {
        return null;
      }

      return this.resolve(source, anchor, { ...options, skipSelf: true });
    },
  };
}

const aliases = [
  { find: /^@osdu\//, replacement: `${forwardSlashes(moduleSrc)}/` },
  { find: /^@\//, replacement: `${forwardSlashes(sqlflowSrc)}/` },
];

/** The virtual module the assertion dashboard's download imports its script and styles from. */
const DASHBOARD_EXPORT = "virtual:assertion-dashboard-export";

/**
 * Bundles the assertion dashboard into one script and its styles, which a downloaded dashboard carries
 * (osdu/docs/reference/flow/assertion-sets.md, Download the dashboard): the same component the workbench renders, built
 * from the same sources with React in production, so the file shows what the page shows and needs nothing to load. The
 * GUI imports the module only when a dashboard is downloaded, so the bundle is a chunk of its own; the dev server builds
 * it on that first import and again when one of its sources changes.
 */
function assertionDashboardExport(): Plugin {
  const resolvedId = `\0${DASHBOARD_EXPORT}`;
  const entry = path.resolve(moduleSrc, "features/delivery/assertions/dashboard/exportEntry.tsx");
  return {
    name: "osdu-gui:assertion-dashboard-export",
    resolveId(source) {
      return source === DASHBOARD_EXPORT ? resolvedId : null;
    },
    async load(id) {
      if (id !== resolvedId) {
        return null;
      }

      const built = await build({
        configFile: false,
        root: guiRoot,
        logLevel: "warn",
        mode: "production",
        plugins: [react()],
        resolve: { alias: aliases },
        define: { "process.env.NODE_ENV": JSON.stringify("production") },
        build: {
          write: false,
          emptyOutDir: false,
          copyPublicDir: false,
          sourcemap: false,
          minify: true,
          cssCodeSplit: false,
          lib: { entry, formats: ["iife"], name: "AssertionDashboard", fileName: () => "assertion-dashboard.js" },
        },
      });
      const outputs = (Array.isArray(built) ? built : [built]).flatMap((result) => ("output" in result ? result.output : []));
      const script = outputs.find((file) => file.type === "chunk");
      if (script === undefined || script.type !== "chunk") {
        throw new Error(`Bundling the assertion dashboard from ${entry} produced no script.`);
      }

      for (const file of script.moduleIds) {
        if (path.isAbsolute(file)) {
          this.addWatchFile(file);
        }
      }

      const styles = outputs.filter((file) => file.type === "asset" && file.fileName.endsWith(".css"))
        .map((file) => (file.type !== "asset" ? "" : typeof file.source === "string" ? file.source : new TextDecoder().decode(file.source)))
        .join("\n");
      return `export const script = ${JSON.stringify(script.code)};\nexport const style = ${JSON.stringify(styles)};\n`;
    },
  };
}

export default defineConfig({
  plugins: [vendoredPackagesFromThisBuild(), assertionDashboardExport(), react(), tailwindcss()],
  resolve: {
    alias: aliases,
  },
  server: {
    port: 5173,
    strictPort: true,
    fs: {
      // The vendored SQLFlow sources, and the module's census files, live outside this folder.
      allow: [guiRoot, sqlflowGui, censusDir],
    },
    watch: {
      // The e2e suite builds its fixture repository under e2e/.fixtures and runs flows that write work files there while
      // the dev server is up; none of it is source the GUI loads.
      ignored: ["**/e2e/**", "**/test-results/**", "**/playwright-report/**"],
    },
  },
  build: {
    outDir: "dist",
    sourcemap: true,
    chunkSizeWarningLimit: 4096, // monaco is deliberately bundled (no CDN), and it is large
  },
});
