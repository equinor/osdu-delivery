---
id: delivery-guide-vscode
title: "The VS Code extension: developing flows, mappings and templates in the editor"
type: guide
summary: "The OSDU Delivery extension: Microsoft browser sign-in, every document understood, mappings checked against templates, the estate, and the MCP server built in."
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
  - document view
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
---

# The VS Code extension

`osdu-delivery-vscode` makes the editor the place a pipeline is developed. Open a repository and every flow, mapping,
template and schedule in it is understood; sign in once and the estate, its runs and an AI assistant are there, all
reading the same [MCP server](mcp.md). It is SQLFlow's extension composed with the delivery module, as the
[MCP server](mcp.md) and the GUI are.

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
| **New Document** | Starts any kind from a skeleton the language engine accepts. |
| **New Mapping from a Template** | Scaffolds a mapping for a template a synced repository holds. |
| **Fetch a Template into This Repository** | Picks a release and a kind from the OSDU data definitions and writes the template file into the template folder on your path, as the GUI's Templates page downloads one. |
| **Compare Two Versions of a Kind** | Says what a mapping has to change to move from one version to another, picked from what the releases publish. |

**Validate Repository** checks every document with the same engine, offline, and lists what it finds in the Problems panel
for files nobody has open.

## What the views show

| View | Shows |
| --- | --- |
| **Document** | Follows the editor: what the document is, whether the file differs from what the estate runs, its latest runs, what runs before and after it, its columns, and for its kind its records, interfaces, target, issues, submissions, audit trail, template or the flows rendering it, or an AI context's approval. A flow's issues group its blocked records by cause. |
| **Repository** | The workspace's documents by kind, marking what is in the estate. |
| **Delivery** | Partitions, mappings, templates, caches and their changes, assertions, dimensions, inventories, removals and configuration. |
| **Estate** | Repositories with their synced commit and any sync error, and their flows. **Sync Repository** syncs one now. |
| **Runs**, **Schedules**, **Data Streams** | What ran, what will, and which tables stopped receiving data. |

A run opens in a panel with its error, the statement that failed, its trace, assertions, files and statements. The lenses
at the top of a document run it, say how it last ran and **Compare with Synced** shows what your file changes from what
runs. **Show Flow History** lists the commits that touched a flow, and **Open in GUI** opens its page when `sqlflow.guiUrl`
is set.

**Run** asks what the run should do, from the operations the control plane registers, asks for the partition of a flow
serving several and for the flow's parameters, offers what that operation takes (force, a submission, only some records,
interfaces or types, what to send again), confirms an operation that writes to the flow's target, and follows the run to
its end.

**Run a Command Line Verb** runs `validate`, [`check`](../cli/check.md), [`preview`](../cli/preview.md) and
[`values`](../cli/values.md) on the open file as a task, on this machine. They read your working copy, so they answer for
what is not pushed yet. A record's rendered content and what OSDU holds
are read this way or in the GUI: the [MCP server](mcp.md#metadata-and-a--questions-query) reads neither, so neither do the
views.

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
| `sqlflow.guiUrl` | The GUI's public address, which makes links into it absolute. |
| `sqlflow.cliPath` | The command line. Empty uses the one the extension carries, else `sqlflow` on the PATH. A machine setting: a workspace cannot name it. |
| `sqlflow.lspBinaryPath`, `sqlflow.mcpBinaryPath` | A build of either server instead of the packaged one. Machine settings too. |
| `sqlflow.recognizeDocuments` | Open YAML files by what they hold. On by default. |
| `sqlflow.runResultLimit`, `sqlflow.dataStreamWindowDays`, `sqlflow.dataStreamShowHealthy` | What the Runs and Data Streams views list. |

## Building it

```bash
export CARGO_TARGET_DIR="$PWD/target"
cargo build --release --manifest-path sqlflow/tools/Cargo.toml -p sqlflow-lsp
cargo build --release --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml
cd osdu/hosts/osdu-delivery-vscode
npm ci
npm run package     # a .vsix for this platform, holding the language server, the MCP server and the command line
```

The package holds all three programs for the platform it is built on, and a build without one of them stops. F5 from this folder runs the extension from a checkout,
finding the servers in their target folders. It replaces SQLFlow's own extension, whose commands it carries, so install
one of the two.

```bash
npm test                      # the unit tests
npm run test:integration      # the extension in a real editor, over the welldb sample, with both servers
```

## See also

- [The MCP server](mcp.md)
- [Writing a mapping](writing-a-mapping.md)
- [Templates](../concepts/templates.md)
- [Authentication and identity](../concepts/authentication-and-identity.md)
- [Decision 0039: the extension is a client of the MCP server](../../wiki/decisions/0039-vscode-extension-is-an-mcp-client.md)
