---
id: delivery-guide-vscode
title: "The VS Code extension: developing flows, mappings and templates in the editor"
type: guide
summary: "The OSDU Delivery extension: every document understood, a mapping previewed from its file, the GUI's pages in the editor's tabs, and the MCP server built in."
keywords:
  - vs code
  - vscode
  - extension
  - editor
  - single sign-on
  - sign in with microsoft
  - browser sign-in
  - device approval
  - mapping completion
  - template folder
  - preview mapping
  - working copy
  - side bar
  - pages in tabs
  - webview
  - marketplace
  - vsix
  - new document
  - validate repository
  - compare with synced
  - github copilot
  - claude code
  - osdu-delivery-vscode
  - sqlflow login
  - credentials.json
  - get_cli
  - fetch a template
  - sqlflow.controlPlaneUrl
  - sqlflow.guiUrl
  - sqlflow.cliPath
related:
  - delivery-guide-mcp
  - delivery-guide-writing-a-mapping
  - delivery-concept-templates
  - delivery-concept-authentication-and-identity
  - delivery-wiki-0039-vscode-extension-is-an-mcp-client
  - delivery-wiki-0060-vscode-shows-the-gui
sourceRefs:
  - osdu/hosts/osdu-delivery-vscode/package.json
  - osdu/hosts/osdu-delivery-vscode/src/extension.ts
  - osdu/hosts/osdu-delivery-vscode/src/deliveryModule.ts
  - osdu/hosts/osdu-delivery-vscode/src/mappingEditing.ts
  - osdu/hosts/osdu-delivery-vscode/src/templates.ts
  - osdu/hosts/osdu-delivery-vscode/src/skeletons.ts
  - sqlflow/tools/sqlflow-vscode/src/host.ts
  - sqlflow/tools/sqlflow-vscode/src/mcp/mcpServer.ts
  - sqlflow/tools/sqlflow-vscode/src/mcp/mcpSetup.ts
  - sqlflow/tools/sqlflow-vscode/src/mcp/signIn.ts
  - sqlflow/tools/sqlflow-vscode/src/cli.ts
  - sqlflow/tools/sqlflow-mcp/src/config.rs
  - sqlflow/src/SqlFlow.Cli/Remote/CredentialStore.cs
  - osdu/hosts/osdu-delivery-vscode/src/deliveryRuns.ts
  - osdu/hosts/osdu-delivery-vscode/src/deliverySchemas.ts
  - sqlflow/tools/sqlflow-vscode/src/documents/recognition.ts
  - sqlflow/gui/src/auth/DeviceApprovalPage.tsx
  - sqlflow/src/SqlFlow.ControlPlane/Security/DeviceApprovalPage.cs
  - sqlflow/gui/src/embed/protocol.ts
  - sqlflow/gui/src/embed/renderHosted.tsx
  - sqlflow/gui/src/embed/HostedSideBar.tsx
  - sqlflow/gui/src/embed/host-design.css
  - sqlflow/tools/sqlflow-vscode/src/gui/carrier.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/credentials.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/hostedHtml.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/hostedLink.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/hostedPages.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/hostedSideBar.ts
  - osdu/gui/src/hosted/WorkingCopyMappingPage.tsx
  - osdu/gui/src/hosted/workingCopy.ts
  - osdu/gui/vite.hosted.config.ts
  - osdu/hosts/osdu-delivery-vscode/package.mjs
  - .github/workflows/vscode-extension.yml
---

# The VS Code extension

`osdu-delivery-vscode` makes the editor the place a pipeline is developed. Open a repository and every flow, mapping,
template and schedule in it is understood; sign in once and the estate's pages open in tabs beside your files, a mapping
shows what it makes as you write it, and an AI assistant reads the estate through the [MCP server](mcp.md). It is
SQLFlow's extension composed with the delivery module, as the [MCP server](mcp.md) and the GUI are.

It draws its own surfaces: one side bar and the GUI's pages, in the colours of your editor theme, and no tree view or
panel of the editor's own.

## Signing in

**OSDU Delivery: Sign In with the Browser** (or the status bar) opens your browser on the control plane. The control
plane passes you to the GUI's approval page, where you sign in the way you always do, **Microsoft single sign-on**
included, compare the code with the one the editor shows (it is on your clipboard), and approve. The editor is signed in
a moment later.

- It needs no app registration of its own: it is the GUI's sign-in, so the GUI's origin stays the one redirect URI
  Microsoft Entra needs ([deployment](deployment.md)).
- Approving takes a click on a code you can compare, so a link someone sends you cannot sign anyone in as you.
- You sign in once. `~/.sqlflow/credentials.json` keeps one credential per control plane, and this editor, its chat, any
  assistant you connect and the `sqlflow` command line all read it, so `sqlflow login` signs the editor in as well as the
  other way round, in every window. A session renews itself up to the control plane's limit, from whichever of them is
  running.
- **OSDU Delivery: Sign In with an Access Token** takes a pasted token, for a place no browser reaches.

The control plane sends the browser to the GUI when it knows the GUI's address:
`ControlPlane:Notifications:GuiBaseUrl`, or else the one origin `ControlPlane:Cors:AllowedOrigins` names, which is the
GUI on every deployment this repository describes. With several origins and no base URL it answers with its own page,
which takes a local account's password and cannot serve an account that signs in with Microsoft alone: set the base URL.

## Writing documents

A file is known by what it holds, as the estate's sync knows it, so a repository works as it is: a `*.yaml` file with a
`flowType`, a `documentType`, a root `schedules`, `subscribers` or `aiContext`, or a `source` and a `target`, opens as a
pipeline document, and any other YAML stays plain YAML. `sqlflow.recognizeDocuments` turns this off.

| In the editor | What it does |
| --- | --- |
| Completion, hover and problems | Every key of every kind, from the same [key census](mcp.md#documentation-and-flow-language) as the GUI's editor and `validate_flow`. |
| A mapping's `record` | Completes the properties of its template as you type, required ones first; a hover says what the schema says; a key the template lacks, and a required property nothing fills, are reported as you go. It reads the [template folders](../concepts/templates.md#template-folders) on the mapping's path in your working copy, so it works offline and before anything is pushed. |
| Values | `render.mapping` offers the repository's mappings, `template.kind` its templates, `schedule` its schedules, `connection` what other flows connect to, `partitions` the partitions, `$param.` the document's parameters and `$cache.` what its cache flows declare. `$from`, `dataset.key` and a property's bare column offer the columns of the table the mapping's delivery flow reads, once the estate has seen it. |
| Links | Ctrl+click a mapping a flow pins, a template a mapping fills, or a schedule a flow joins. |
| **Preview Mapping** (Ctrl+K V) | Opens the mapping's page beside its file, read from the file: see [Previewing a mapping](#previewing-a-mapping). |
| **New Document** | Starts any kind from a skeleton the language engine accepts. |
| **New Mapping from a Template** | Scaffolds a mapping for a template a synced repository holds. |
| **Fetch a Template into This Repository** | Picks a release and a kind from the OSDU data definitions and writes the template file into the template folder on your path, as the GUI's Templates page downloads one. |
| **Open Templates** | Opens the Templates page, which compares two versions of a kind and says what a mapping has to change to move from one to the other. |

**Validate Repository** checks every document with the same engine, offline, and lists what it finds in the Problems panel
for files nobody has open.

## The side bar and the pages

The extension's side bar has two parts, with a search over them and who is signed in beside it.

| Part | Shows |
| --- | --- |
| **Working copy** | The documents of the folder open in the editor, by kind, read from disk: flows by their kind, mappings, dictionaries, schedules. A row opens its file, and offers what can be done with it on the row and in its menu: run a flow, open its page, compare it with what the estate runs, preview a mapping. A flow the estate does not have yet is marked, and so is the file in front of you when it differs from what runs. It needs no sign-in. |
| **Estate** | The GUI's own menu. Every entry opens its page in a tab of the editor. |

Typing in the search narrows the working copy to the documents with a word starting that way, in their name or their
path; Enter searches the estate.

A page in a tab is the page [the GUI](../concepts/gui.md) shows, with the same data and the same actions, under your
sign-in: a flow with its records, issues, submissions and preview, a run with its trace as it is written, lineage,
schedules, templates, the cache, a record's history. A strip over the page goes back and forward through what the tab has
shown, reads the page again, carries the partition every delivery page is read in, and opens the same page in your
browser when `sqlflow.guiUrl` is set. A page already open is brought forward rather than opened twice, and the tabs open
when the editor closes come back on the pages they showed.

| Command | Opens |
| --- | --- |
| **Open Home**, **Open Runs**, **Open Lineage** | The home page, the runs board, the lineage graph. |
| **Open Delivery**, **Open Templates**, **Find a Record** | The delivery overview, the Templates page, the record lookup. |
| **Open Flow Page** | The page of the flow in the editor, beside its file. Also a lens at the top of a flow. |

The lenses at the top of a flow run it, say how it last ran, open its page, and **Compare with Synced** shows what your
file changes from what runs. **Open in the Browser** opens the flow's page in the web GUI when `sqlflow.guiUrl` is set.

### Previewing a mapping

**Preview Mapping** opens the page of the mapping in the editor, beside its file. It is the
[mapping's page](../concepts/gui.md) with one difference: it reads the file.

- **Overview** lays out what the mapping does, from the tables it reads to the record it fills
  ([the mapping overview](../concepts/mapping-overview.md)), **Properties** lays the mapping over its template attribute by
  attribute, and **Record shape** draws the record it renders. All three are drawn from the text of your file, so they
  show a mapping before it is pushed.
- **Staged records** and the **Dry run** read rows, and a row is rendered by the control plane with the mapping the estate
  last synced ([a dry run](../concepts/dry-run.md)). When your file differs from that mapping the page says so: push and
  sync to render rows with the new text.
- The page follows the file as you save it. While the editor holds changes you have not saved it shows the file as last
  saved and says so, with a link to show the unsaved text at once, so a line half typed never redraws the page.
- A mapping the estate has not seen yet shows Properties and Record shape, with its template settled from the template
  folders of the repository its neighbours were synced from.

### Running a flow

**Run** asks what the run should do, from the operations the control plane registers, asks for the partition of a flow
serving several and for the flow's parameters, offers what that operation takes (force, a submission, only some records,
interfaces or types, what to send again), and confirms an operation that writes to the flow's target. The run's own page
then opens beside the file and follows the run: its trace as it is written, and what it reported when it ended.

**Run a Command Line Verb** runs `validate`, [`check`](../cli/check.md), [`preview`](../cli/preview.md) and
[`values`](../cli/values.md) on the open file as a task, on this machine. They read your working copy whole, flow and
mapping alike, so they render a record with a mapping that is not pushed yet, where a page's rows wait for the sync.

### What a page may reach

A page is a webview, and is given nothing but the extension's own files to load. It holds no credential, and its content
security policy lets it connect to no address at all. Every call it makes to the control plane is made for it by the
extension, with the credential you signed in with, to the control plane you signed in to and nowhere else. So a page
shows exactly what your account may see in the GUI, a record's content included, and nothing of it reaches an assistant:
what [the MCP server](mcp.md#metadata-and-a--questions-query) tells a model is unchanged.

## The assistant

Nothing needs installing. The extension registers the server with VS Code once per [mode](mcp.md#installing-it-for-a-person):
**OSDU Delivery (estate)** and **OSDU Delivery (data)** appear among the chat's tools, signed in with the editor. Turn on
the one a chat needs.

**Connect an AI Assistant** connects Claude Code, Cursor or Codex. It copies the server to `~/.sqlflow/bin`, so the
registration survives the extension's updates, and takes the registration from the server's own `install`: for Claude
Code it puts the command in a terminal for you to run, and for the others it shows what to add. It edits no other tool's
configuration by itself.

## Scripting with the command line

The package carries the `sqlflow` command line beside the language server and the MCP server, so an assistant can do by
script what a person would click through: validate a working copy, check a flow, sync, run and read back, in a loop. Every
integrated terminal has it on its PATH, pointed at the control plane the editor uses, and the MCP tool `get_cli` tells an
assistant where it is, wherever that assistant runs. It signs in with [the same credential](#signing-in), so a script needs
no token.

```bash
sqlflow login                       # opens the browser, if you have not signed in yet
sqlflow validate .                  # every document of the working copy
sqlflow check flows/wells.yaml      # would it render?
sqlflow trigger --repo welldb --flow welldb-well-03-delivery --follow
```

`sqlflow login --device` prints the link instead of opening a browser, for a machine with none. Outside the editor a script
needs no `--url` either while the credential file holds one control plane.

The MCP server's rules hold for the assistant here too: a verb that writes runs only when you asked for it. `preview` and
`values` print row values, which then reach the assistant and its model provider, so it runs them only when you ask.

## Settings

| Setting | Meaning |
| --- | --- |
| `sqlflow.controlPlaneUrl` | The control plane. Empty uses the address the server was last given. An untrusted workspace cannot set it, so a repository cannot choose where your sign-in goes. |
| `sqlflow.guiUrl` | The GUI's public address. With it a page offers itself in the browser, and links into the GUI are absolute. |
| `sqlflow.cliPath` | The command line. Empty uses the one the extension carries, else `sqlflow` on the PATH. A machine setting: a workspace cannot name it. |
| `sqlflow.lspBinaryPath`, `sqlflow.mcpBinaryPath` | A build of either server instead of the packaged one. Machine settings too. |
| `sqlflow.recognizeDocuments` | Open YAML files by what they hold. On by default. |

## Building it

```bash
(cd osdu/gui && npm ci)          # the pages are built from the GUI's sources, with its packages
cd osdu/hosts/osdu-delivery-vscode
npm ci
npm run package                  # builds the three programs, the pages and the bundle, and writes a .vsix for this platform
```

A package holds three programs built for its platform (the language server, the MCP server and the command line), so
there is one package per platform: `win32-x64`, `linux-x64`, `darwin-arm64` and `darwin-x64`. `npm run package` builds
the one for the machine it runs on, and stops when a program is missing. `node package.mjs --skip-programs` packages again
from the programs already built.

`npm run bundle` builds the extension for a checkout: F5 from this folder then runs it, finding the two servers in their
target folders. It builds the pages when the folder has none; `npm run bundle:hosted` builds them again after the GUI
changed. The extension replaces SQLFlow's own, whose commands it carries, so install one of the two.

```bash
npm test                      # the unit tests
npm run test:integration      # the extension in a real editor, over the welldb sample, with both servers
```

With `OSDU_VSCODE_TEST_CONTROL_PLANE` and `OSDU_VSCODE_TEST_TOKEN` set, the integration suite also opens pages against
that control plane and checks that each one loads, is signed in and names its tab.

### Publishing it

The workflow **VS Code extension** (`.github/workflows/vscode-extension.yml`) builds the package of every platform, each
on a runner of that platform, and keeps them as artifacts. It runs by hand only. Ticking **publish** sends the packages to
the Visual Studio Marketplace, which needs the repository secret `VSCE_PAT`: a personal access token of the publisher the
manifest names, with the Marketplace scope Manage. The Marketplace takes a version once, so raise `version` in
`osdu/hosts/osdu-delivery-vscode/package.json` and add its section to the extension's `CHANGELOG.md` before the next one.

## See also

- [The MCP server](mcp.md)
- [Writing a mapping](writing-a-mapping.md)
- [Templates](../concepts/templates.md)
- [Authentication and identity](../concepts/authentication-and-identity.md)
- [The GUI](../concepts/gui.md)
- [Decision 0039: the extension signs in through the MCP server](../../wiki/decisions/0039-vscode-extension-is-an-mcp-client.md)
- [Decision 0060: the extension shows the GUI's own pages](../../wiki/decisions/0060-vscode-shows-the-gui.md)
