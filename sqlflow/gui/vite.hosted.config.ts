import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { hostedBuild, workersInline } from "./hosted.build.ts";

// The hosted build (src/embed/): the workbench's pages and a side bar for an editor's webviews, written into the folder
// SQLFlow's VS Code extension serves them from (tools/sqlflow-vscode/dist/hosted). Nothing is copied from public/: a
// hosted page has no config.json, since its host says where the control plane is, and the entries import the brand
// images they show. `base` is relative because the folder is served from an address only the editor knows.
const here = (path: string) => fileURLToPath(new URL(path, import.meta.url));

export default defineConfig({
  base: "./",
  publicDir: false,
  plugins: [workersInline(), react(), tailwindcss()],
  resolve: {
    alias: {
      "@": here("./src"),
    },
  },
  build: hostedBuild(
    { page: here("./src/embed/page.tsx"), sideBar: here("./src/embed/side-bar.tsx") },
    here("../tools/sqlflow-vscode/dist/hosted"),
  ),
});
