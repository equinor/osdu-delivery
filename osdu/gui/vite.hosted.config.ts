import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { hostedBuild, workersInline } from "../../sqlflow/gui/hosted.build.ts";
import { aliases, assertionDashboardExport, vendoredPackagesFromThisBuild } from "./vite.config.ts";

// The hosted build (src/hosted/): the pages and the side bar the OSDU Delivery extension shows in VS Code, built from
// the sources the browser's GUI is built from, with the same aliases and the same one copy of every package, and
// written into the folder the extension serves them from (osdu/hosts/osdu-delivery-vscode/dist/hosted). SQLFlow's
// hosted.build.ts lays the folder out, so the extension finds what SQLFlow's own extension finds in its own.
//
// Nothing is copied from public/: a hosted page has no config.json, since the extension says where the control plane
// is, and the entries import the brand images they show. `base` is relative because the folder is served from an
// address only the editor knows.

const guiRoot = fileURLToPath(new URL(".", import.meta.url));

export default defineConfig({
  base: "./",
  publicDir: false,
  plugins: [workersInline(), vendoredPackagesFromThisBuild(), assertionDashboardExport(), react(), tailwindcss()],
  resolve: {
    alias: aliases,
  },
  build: hostedBuild(
    {
      page: path.resolve(guiRoot, "src/hosted/page.tsx"),
      sideBar: path.resolve(guiRoot, "src/hosted/side-bar.tsx"),
    },
    path.resolve(guiRoot, "../hosts/osdu-delivery-vscode/dist/hosted"),
  ),
});
