# OSDU Delivery, powered by SQLFlow

Write the pipelines that deliver subsurface records (wells, wellbores, well logs and the rest) into an OSDU® data
platform, where you write code. Every flow, mapping, template and schedule is understood as you type, a mapping shows
what it makes while you write it, and the pages of your estate (runs, lineage, records, the ledger) open in tabs beside
your files.

OSDU Delivery is an independent product built on SQLFlow. It is not affiliated with, sponsored by or endorsed by The
Open Group or the OSDU® Forum ([trademarks](#trademarks)).

![A mapping's file with its preview beside it](https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/osdu/hosts/osdu-delivery-vscode/media/mapping-preview.png)

## What you get

**A YAML editor that knows the product.** Completion, hover and problems for every key of every document kind, from the
same language engine the control plane validates with. A file is recognised by what it holds, so a repository works as
it is, with no renaming.

**Mappings checked against their template as you type.** A mapping's `record` completes the properties of the template
it fills, required ones first. A key the template does not have, and a required property nothing fills, are reported
on the line. It reads the template folders in your working copy, so it works offline and before anything is pushed.

**A preview of the mapping you are writing.** `Preview Mapping` (Ctrl+K V) opens the mapping's page beside its file: the
tables it reads on the left, the record it fills on the right and one line per property between them, the staged
records with the document each one renders to, and every property of the template with what fills it. The preview
reads your file, and follows it as you save.

![The properties of a template with what a mapping fills](https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/osdu/hosts/osdu-delivery-vscode/media/mapping-properties.png)

**Your estate in the editor.** The side bar is the web GUI's own navigation: its search, its menu, and a button that
syncs your repository on the control plane you are signed in to, after you confirm. Every page is the one the web GUI shows, in the colours of your editor
theme: a flow with its records, issues and submissions, a run with its trace as it is written, lineage, schedules,
templates, the cache, and a record's whole history.

![A delivery flow's file with the flow's page beside it](https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/osdu/hosts/osdu-delivery-vscode/media/flow-page.png)

**An AI assistant that knows your estate.** The extension registers the OSDU Delivery MCP server with the editor's chat,
signed in as you, and connects Claude Code, Cursor or Codex to the same server in one command. The server answers from
the estate's metadata; your records' content is never sent to a model unless you ask a data question an administrator
has opened.

**The command line, ready for scripts.** Every integrated terminal has `sqlflow` on its path, signed in with the same
credential, so you or an assistant can validate a working copy, check a flow, run it and read the result in a loop.

## Getting started

1. Install the extension and open the folder of a pipeline repository.
2. Run **OSDU Delivery: Set Control Plane URL** and give the address of your control plane.
3. Run **OSDU Delivery: Sign In with the Browser**. Your browser opens the sign-in you always use, Microsoft single
   sign-on included; compare the code with the one the editor shows and approve. The editor, its chat, any assistant you
   connect and the `sqlflow` command line share the sign-in.
4. Open a mapping and press Ctrl+K V, or pick a page from the side bar.

Everything that reads only your files (completion, hover, problems, links, `Validate Repository`) works with no control
plane and no sign-in.

## Requirements

- VS Code 1.101 or later, on Windows x64, Linux x64, macOS arm64 or macOS x64.
- An OSDU Delivery control plane you can sign in to, for the estate's pages, the assistant and running flows.

The package carries its own language server, MCP server and `sqlflow` command line for your platform, signed on Windows
and signed and notarized on macOS. Nothing else needs installing.

## Commands

| Command | What it does |
| --- | --- |
| Preview Mapping | Opens the mapping's page beside its file, read from the file. |
| Open Flow Page | Opens the page of the flow in the editor: its records, issues, runs and schedules. |
| Compare with Synced | Shows what your file changes from what the estate runs. |
| Validate Repository | Checks every document of the working copy and lists what it finds in Problems. |
| New Document, New Mapping from a Template | Starts a document from a skeleton, or a mapping for a template. |
| Fetch a Template into This Repository | Writes a template from the OSDU data definitions into your template folder. |
| Open Home, Open Runs, Open Lineage, Open Delivery, Open Templates, Find a Record | Open a page of the estate. |
| Connect an AI Assistant | Registers the MCP server with Claude Code, Cursor or Codex. |
| Run a Command Line Verb on This Document | Runs `validate`, `check`, `preview` or `values` on the open file. |

## Settings

| Setting | Meaning |
| --- | --- |
| `sqlflow.controlPlaneUrl` | The control plane. A workspace you have not trusted cannot set it. |
| `sqlflow.guiUrl` | The web GUI's address. With it, a page shown here offers itself in the browser. |
| `sqlflow.recognizeDocuments` | Open YAML files as pipeline documents by what they hold. On by default. |
| `sqlflow.cliPath`, `sqlflow.lspBinaryPath`, `sqlflow.mcpBinaryPath` | A build of a program to run instead of the packaged one. Machine settings: a workspace cannot name a program. |

## Your sign-in and your data

- Your credential is kept in `~/.sqlflow/credentials.json`, readable by you alone, and is the one the `sqlflow` command
  line uses. The extension stores no credential of its own.
- A page calls the control plane through the extension, under your sign-in. The page itself is given no credential and
  its content security policy lets it reach no address at all, so it can show you only what your account may see.
- The extension collects no telemetry.

## More

- [The guide](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/guides/vscode.md)
- [The MCP server](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/guides/mcp.md)
- [Writing a mapping](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/guides/writing-a-mapping.md)
- [Report a problem](https://github.com/TahirRiaz/osdu-delivery/issues)

OSDU Delivery is free software under the GNU General Public License, version 3 or later. The third-party components
the package carries, and their licenses, are listed in `THIRD-PARTY-NOTICES.txt` inside it.

## Trademarks

OSDU is a registered trademark of The Open Group. OSDU Delivery is an independent product built on SQLFlow, and is
not affiliated with, sponsored by, endorsed by or certified by The Open Group or the OSDU® Forum.
