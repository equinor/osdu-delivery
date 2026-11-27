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
  - sync repository
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
  - get_environment
  - other tools
  - user path
  - claude code registration
  - fetch a template
  - sqlflow.controlPlaneUrl
  - sqlflow.environments
  - sqlflow.guiUrl
  - sqlflow.cliPath
related:
  - delivery-guide-vscode-environments
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
  - sqlflow/tools/sqlflow-vscode/src/otherTools.ts
  - sqlflow/tools/sqlflow-vscode/src/userPath.ts
  - sqlflow/tools/sqlflow-vscode/src/mcp/claudeCode.ts
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
  - .github/workflows/vscode-build.yml
  - .github/workflows/vscode-publish.yml
  - osdu/hosts/osdu-delivery-vscode/notices.mts
---

# The VS Code extension

`osdu-delivery-vscode` makes the editor the place a pipeline is developed. Open a repository and every flow, mapping,
template and schedule in it is understood; sign in once and the estate's pages open in tabs beside your files, a mapping
shows what it makes as you write it, and an AI assistant reads the estate through the [MCP server](mcp.md). It is
SQLFlow's extension composed with the delivery module, as the [MCP server](mcp.md) and the GUI are.

It draws its own surfaces: one side bar and the GUI's pages, in the colours of your editor theme, and no tree view or
panel of the editor's own.

## Signing in

Until you sign in, the side bar is the way in: the product, the [environment](vscode-environments.md) you sign in to,
in its stage's colour, with whether it answers (**Switch** or **Change** leads to your environments), and **Sign in**. A
control plane that does not answer says so there, before your browser opens on nothing.

**Sign in** (or **OSDU Delivery: Sign In with the Browser**, or the status bar) opens your browser on the control plane.
The control plane passes you to the GUI's approval page, which stands on the login page's brand field: you sign in the way
you always do, **Microsoft single sign-on** included, see the account the client will act as and what it may do, compare
the code with the one the side bar shows (it is on your clipboard too), and approve. Meanwhile the side bar shows that
code, with **Open the page again** for a page you closed and **Cancel** (**OSDU Delivery: Cancel the Sign-In**); signing
in again while one waits opens the same page rather than a second code. The editor is signed in a moment after you
approve, and the side bar turns into the menu by itself.

- It needs no app registration of its own: it is the GUI's sign-in, so the GUI's origin stays the one redirect URI
  Microsoft Entra needs ([deployment](deployment.md)).
- Approving takes a click on a code you can compare, so a link someone sends you cannot sign anyone in as you.
- You sign in once. `~/.sqlflow/credentials.json` keeps one credential per control plane, and this editor, its chat, any
  assistant you connect and the `sqlflow` command line all read it, so `sqlflow login` signs the editor in as well as the
  other way round, in every window. A session renews itself up to the control plane's limit, from whichever of them is
  running.
- The control plane in your settings is every server's of this product: one a chat or Claude Code started before you
  named it moves to it on its next call and takes your sign-in for it, and naming another control plane in a chat takes
  the sign-in held for that one, never this one's.
- Signing in also gives your other tools the command line and the server ([your other tools](#your-other-tools)).
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

The extension's side bar is the GUI's own navigation once you are signed in. Across its top is the
[environment](vscode-environments.md) you work against, in its stage's colour (development green, test orange, production
red), with the menu that switches to another, adds one or manages them. Its header holds the search, which opens
the search page; a **Sync** button; and who is signed in. Under it is the GUI's menu, and every entry opens its
page in a tab of the editor. The files of the folder you have open are in the editor's own Explorer, and a flow is run from
its page or from the lens at the top of its file.

**Sync** has the control plane you are signed in to read the latest commit of a repository's branch: it is how a commit
you pushed reaches that environment, and runs started afterwards use it. Its tooltip names the repository, the commit it
holds and the control plane. A repository that syncs only on request moves from the commit it holds only after you
confirm, in the words the Repos page confirms it with. With several repositories registered, the button opens a menu of
them, each with how its last sync went. When the sync ends the editor reads the control plane again, so a flow's lenses
and **Compare with Synced** follow it.

A page in a tab is the page [the GUI](../concepts/gui.md) shows, with the same data and the same actions, under your
sign-in: a flow with its records, issues, submissions and preview, a run with its trace as it is written, lineage,
schedules, templates, the cache, a record's history, and the estate's [reports](../../../../sqlflow/docs/reference/concepts/reports.md). A strip over the page goes back and forward through what the tab has
shown, reads the page again, carries the partition every delivery page is read in, and opens the same page in your
browser when `sqlflow.guiUrl` is set. A page already open is brought forward rather than opened twice, and the tabs open
when the editor closes come back on the pages they showed. A page behind another tab, and the side bar while it is
closed, ask the control plane nothing until they are in front again, when they read what went stale: pages left open
spend none of the calls a minute you are allowed
([rate limiting](../../../../sqlflow/docs/reference/concepts/control-plane.md#health-probes-and-rate-limiting)).

| Command | Opens |
| --- | --- |
| **Open Home**, **Open Runs**, **Open Lineage** | The home page, the runs board, the lineage graph. |
| **Open Reports** | The estate's reports; one opens in a tab, runs its queries and draws their answers. |
| **Open Delivery**, **Open Templates**, **Find a Record** | The delivery overview, the Templates page, the record lookup. |
| **Open Flow Page** | The page of the flow in the editor, beside its file. Also a lens at the top of a flow. On a report it opens the report. |

The lenses at the top of a flow say how it last ran, open its page, and **Compare with Synced** shows what your file
changes from what runs. A report (`flowType: rpt`) is never run as a flow, so it has no last run: its lens is **Report
Page**, which opens the report beside the file, where its queries run when the page opens. **Open in the Browser** opens the flow's page in the web GUI when `sqlflow.guiUrl` is set.

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
- A mapping the estate has not seen yet, such as a new file, has the same page: Overview, Properties and Record shape
  from its text, with its template settled from the template folders of the repository its neighbours were synced from,
  and Staged records and the dry run saying they wait until it is pushed and synced.

### Running a flow

A flow runs from its page, as in the GUI: **Open Flow Page** (or the lens at the top of the flow) opens it beside the file,
and its run form asks what the run should do and confirms an operation that writes to the flow's target. The run's page
follows it: its trace as it is written, and what it reported when it ended.

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

Signing in registers the server with Claude Code too, when it is installed: once per mode, for all its sessions
([your other tools](#your-other-tools)). **Connect an AI Assistant** does that on request, and for Cursor or Codex shows
the registration to add. Each registration is the server's own `install`, and runs the server from `~/.sqlflow/bin`, so it
survives the extension's updates.

## Scripting with the command line

The package carries the `sqlflow` command line beside the language server and the MCP server, so an assistant can do by
script what a person would click through: validate a working copy, check a flow, sync, run and read back, in a loop. Every
integrated terminal has it on its PATH, pointed at the control plane the editor uses, and once you sign in every other
terminal has it too ([your other tools](#your-other-tools)). The MCP tool `get_environment` tells an assistant how to call
it where that assistant runs, which control plane it reaches, and what its verbs do. It signs in with
[the same credential](#signing-in), so a script needs no token.

```bash
sqlflow login                       # opens the browser, if you have not signed in yet
sqlflow validate .                  # every document of the working copy
sqlflow check flows/wells.yaml      # would it render?
sqlflow trigger --repo welldb --flow welldb-well-03-delivery --follow
```

`sqlflow login --device` prints the link instead of opening a browser, for a machine with none. Outside the editor a script
needs no `--url` either: it reaches your current control plane, the one you last signed in to here or named with
`sqlflow use` ([which control plane a command reaches](../../../../sqlflow/docs/reference/cli/control-plane.md#signing-in)).

The MCP server's rules hold for the assistant here too: a verb that writes runs only when you asked for it. `preview` and
`values` print row values, which then reach the assistant and its model provider, so it runs them only when you ask.

## Your other tools

Signing in makes the extension's programs available beyond the editor, so a Copilot or Claude Code session, a terminal
of your own and a script have them too, signed in as you:

- **The command line on your PATH.** `sqlflow` and the MCP server are copied to `~/.sqlflow/bin`, which goes at the end
  of your own PATH: on Windows the user environment's `Path`, every other entry kept as it was written; on macOS and Linux
  a line in `.profile` (and in `.bash_profile`, `.bashrc` and `.zshenv` where they apply) that reads
  `~/.sqlflow/path.sh`, and a file in fish's `conf.d`. A `sqlflow` you put on your PATH yourself keeps answering first.
- **The server in Claude Code.** When Claude Code is installed, both [modes](mcp.md#installing-it-for-a-person) of the
  server are registered for all its sessions, with the control plane the editor uses.
- **One folder to allow.** The chat's servers run from `~/.sqlflow/bin` too, so an assistant is pointed to the command
  line there, never into the extension's folder, whose name changes with every version. An assistant whose shell is
  sandboxed needs read and execute on `~/.sqlflow/bin`, and read and write on `~/.sqlflow/credentials.json`, the sign-in
  it shares; `get_environment` names both.
- **Your control plane, with no environment variable.** Signing in here, or switching the control plane in your settings,
  makes it your current control plane, which every shell and script reaches with no `--url`; `sqlflow use` switches it, and
  `sqlflow use` alone says which one a command reaches and why. `SQLFLOW_URL` is set in the editor's own terminals only:
  set for your whole account it would outrank every repository's `.sqlflow/env`.

The extension says what it changed, with **Undo**. It does it again when the control plane in your settings changes, and
an update refreshes `~/.sqlflow/bin` in place. A program already running, VS Code included, has the new PATH once it is
restarted. **Remove the Command Line and Server from Other Tools** takes both out again, and a later sign-in leaves them
out; **Make the Command Line and Server Available to Other Tools** puts them back.

## Settings

| Setting | Meaning |
| --- | --- |
| `sqlflow.controlPlaneUrl` | The control plane. Empty uses the address the server was last given. An untrusted workspace cannot set it, so a repository cannot choose where your sign-in goes. Switching [environments](vscode-environments.md) sets it. |
| `sqlflow.environments` | Your environments: each a control plane's address, its name and its stage, with its GUI's address if you give one ([environments](vscode-environments.md)). Your own settings only: a workspace cannot set it. |
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
from the programs already built, and `--programs-only` builds them and lists where each is, for signing before packaging.

Every package carries `THIRD-PARTY-NOTICES.txt`, written as it is packaged from what the builds left: each npm package
bundled into the pages and the extension, each Rust crate compiled into the two servers, and each NuGet package and the
.NET runtime published into the command line, with its license (`notices.mts`). A component that states no license stops
the package. A version is packaged only once the extension's `CHANGELOG.md` has its section, since the Marketplace shows
it as the release notes.

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

The extension is published as `delta-forge.osdu-delivery`, under DeltaForge's Marketplace publisher, in two workflows run
by hand, as DeltaForge publishes its own extension. **Nothing publishes it on its own.** Both workflows start only when a
person runs them (`workflow_dispatch`): no push, merge, tag, release or schedule builds or publishes the extension, and
no other workflow starts either of them. The extension's own tests hold the workflows to that (`test/manifest.test.ts`).

1. **VS Code extension · build** (`.github/workflows/vscode-build.yml`) builds the three programs on a GitHub runner of
   each platform, signs them, and packages one `.vsix` per platform, kept as artifacts for 30 days. The Windows programs
   are signed with DigiCert KeyLocker; the macOS ones with a Developer ID certificate under the hardened runtime and are
   notarized, the command line with the entitlements its .NET runtime needs (`signing/sqlflow.entitlements.plist`); the
   Linux ones are not signed. Signing is on unless unticked.
2. **VS Code extension · publish** (`.github/workflows/vscode-publish.yml`) publishes the packages of a build, the latest
   successful one unless a run is named. It refuses an unsigned build. A platform the Marketplace already holds at that
   version counts as published, so running it again retries only what failed.

| Secret | For |
| --- | --- |
| `VSCE_PAT` | Publishing: a token of the publisher `delta-forge` with the scope Marketplace (Manage). |
| `SM_HOST`, `SM_API_KEY`, `SM_CLIENT_CERT_FILE_B64`, `SM_CLIENT_CERT_PASSWORD`, `SM_KEYPAIR_ALIAS` | Signing on Windows, with DigiCert KeyLocker. |
| `APPLE_CERT_P12_B64`, `APPLE_CERT_PASSWORD`, `APPLE_ID`, `APPLE_APP_PASSWORD`, `APPLE_TEAM_ID` | Signing and notarizing on macOS. |

They are the secrets DeltaForge's own workflows use. The Marketplace takes a version once, so raise `version` in
`osdu/hosts/osdu-delivery-vscode/package.json` and add its section to the extension's `CHANGELOG.md` before the next one.

## See also

- [The MCP server](mcp.md)
- [Writing a mapping](writing-a-mapping.md)
- [Templates](../concepts/templates.md)
- [Authentication and identity](../concepts/authentication-and-identity.md)
- [The GUI](../concepts/gui.md)
- [Decision 0039: the extension signs in through the MCP server](../../wiki/decisions/0039-vscode-extension-is-an-mcp-client.md)
- [Decision 0060: the extension shows the GUI's own pages](../../wiki/decisions/0060-vscode-shows-the-gui.md)
