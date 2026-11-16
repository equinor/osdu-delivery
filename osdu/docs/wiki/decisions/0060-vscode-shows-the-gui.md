---
id: delivery-wiki-0060-vscode-shows-the-gui
title: "Decision 0060: the VS Code extension shows the GUI's own pages, built a second time and read with the person's sign-in"
type: decision
summary: "Why the extension draws one side bar and the GUI's pages in webviews, carries a page's calls itself, and previews a mapping from its file."
keywords:
  - vs code extension
  - webview
  - hosted build
  - side bar
  - native tree view
  - mapping preview
  - working copy
  - content security policy
  - credential
  - editor theme
  - marketplace
  - platform package
updated: 2026-11-16
sourceRefs:
  - sqlflow/gui/src/embed/protocol.ts
  - sqlflow/gui/src/embed/hostTransport.ts
  - sqlflow/gui/src/embed/renderHosted.tsx
  - sqlflow/gui/src/embed/HostedFrame.tsx
  - sqlflow/gui/src/embed/HostedSideBar.tsx
  - sqlflow/gui/src/embed/host-design.css
  - sqlflow/gui/hosted.build.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/carrier.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/credentials.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/hostedHtml.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/hostedLink.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/hostedPages.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/hostedSideBar.ts
  - sqlflow/tools/sqlflow-vscode/src/host.ts
  - osdu/gui/src/hosted/WorkingCopyMappingPage.tsx
  - osdu/gui/src/hosted/workingCopy.ts
  - osdu/gui/vite.hosted.config.ts
  - osdu/hosts/osdu-delivery-vscode/src/deliveryModule.ts
  - osdu/hosts/osdu-delivery-vscode/package.mjs
  - .github/workflows/vscode-extension.yml
referenceRefs:
  - delivery-guide-vscode
  - delivery-guide-mcp
  - delivery-concept-gui
  - delivery-concept-mapping-overview
  - delivery-concept-dry-run
related:
  - delivery-wiki-0039-vscode-extension-is-an-mcp-client
  - delivery-wiki-0040-dry-run-of-a-mapping
---
# 0060: The VS Code extension shows the GUI's own pages, built a second time and read with the person's sign-in

Status: accepted. Documented in [the VS Code extension](../../reference/guides/vscode.md). It amends
[decision 0039](0039-vscode-extension-is-an-mcp-client.md): the extension still signs in through the MCP server and
shares its one credential, but what it shows is no longer read through the server's tools.

The human's words: "The main goal for the extention is to be a yaml editor. LSP, MCP are key elements. however the user
interface can benefit from using the same rich interface to display infomation. For example the mapping document preview
interface can be very usefull for a vscode extention. I need you to carefully and accuratly prot the rich interfaces to
vscode. I dont want to use the native vscode tools and panels as they are very limited in what they can do and how they
look.", and "I want a production grade vscode extention that i can publish to the market place." Of the tree views the
first version had, earlier: "ugly and it looks like clutter".

The first version drew seven of the editor's own tree views and a plain panel per answer, each filled from an MCP tool.
A tree of rows cannot show a mapping's overview, a record beside what it renders to, or a run's trace as it is written,
and the GUI already draws all three.

## The pages are the GUI's, built a second time

The pages are not ported, they are the same sources. The GUI gains a second entry, the hosted build, which renders its
pages without the workbench's chrome (the editor already has a title bar, an activity bar, tabs and a status bar) and
takes what a browser tab would give it from its host instead. The extension serves that build in webviews: one for the
side bar, one per page.

- **A page added to the GUI is a page in the editor.** The route list is one function both frames call, so nothing has to
  be done for a new page, and nothing can be forgotten.
- **Rejected: copying pages into the extension.** A sister product's extension did this, and its copies had drifted
  from the GUI's pages by tens to hundreds of lines a page. Two copies of a page are two pages.
- **Rejected: framing the deployed GUI.** Microsoft's sign-in refuses to be framed, the frame would need a sign-in of its
  own, nothing would show offline, and a page in a frame cannot read the file in the editor, which is what the
  mapping's preview is for.
- **Rejected: keeping the trees and adding pages beside them.** The side bar would then say everything twice, once as
  rows and once as a page.

## A page's calls are carried by the extension

The GUI's pages call the control plane's REST API, so the hosted pages do too. Decision 0039 kept a REST client out of
the extension because it had no credential of its own and because the views were to hold to the MCP server's rule about
what is metadata. Both reasons are answered differently now.

- **The rule is about what a model is told.** The MCP server withholds a record's content because its answers go to a
  model provider. A page is drawn for the person who signed in and goes nowhere else, and the control plane decides what
  that person may read, as it does in the browser. So a page shows a rendered record; the server's tools, and so every
  assistant, are told exactly what they were told before.
- **The credential is the one the server keeps.** The extension reads the shared credential file where the server and
  the command line read it, each time a call is made, and writes nothing: signing in, renewing and signing out stay the
  server's, which keeps a session alive there while it runs. A fixture holds the three readers to one format.
- **A page holds no credential and can reach nothing.** Its content security policy admits no host at all, and it is
  handed no token. It asks the extension for a call, and the extension makes it only to the control plane it is signed in
  to, by a method the API has, with the headers a page has reason to set, adding the credential itself. What goes wrong
  in a page therefore cannot pick whom a call is made as or where it goes.
- **Rejected: the page calling the control plane itself.** A webview's origin is not one a control plane's CORS allows,
  and the token would have to live in the page.
- **Rejected: a method on the MCP server that hands out the credential, or carries the calls.** It would put a second
  protocol beside the tools for one client, and streaming a run's trace and a file's bytes through it is the transport
  the extension's own process already has.

Actions an assistant has too (running a flow, syncing a repository) still go through the server's tools from the
editor's commands, so the two cannot drift.

## The editor's theme, not a design of the GUI's

A hosted page wears the host design: its surfaces, text, hairlines, the colour for what a person acts on, the focus ring
and the fonts are the editor theme's own variables, so a page reads as part of the editor in whatever theme the person
works in. The status hues and the chart palette do not follow the theme. They are measured values and mean the same thing
wherever a run or a record is shown, so they stay the workbench's own, in the step for the theme's mode.

## One side bar: the working copy, and the GUI's menu

The side bar lists the working copy from disk, so it shows what is being written before any of it is pushed, and under it
the GUI's own menu, each entry opening its page in a tab. What the first version's Document, Runs, Schedules, Data Streams
and Delivery views listed is on those pages, with more than a row could say. A module no longer adds sections of rows: it
adds what a document's row offers, and its pages are its GUI module's.

## A mapping's preview reads the file, and its rows read the sync

The preview is the mapping's page given the text of the file in place of the synced text. The four endpoints behind
Overview, Properties and Record shape take a document's text and read no row, so they show a mapping before it is pushed.
Rows are another matter: a staged row and a dry run are rendered by a node with the mapping the flow's synced checkout
holds ([decision 0040](0040-dry-run-of-a-mapping.md)), so those tabs read the synced mapping, and the page says so when
the file differs from it.

- **It follows the file as saved, not as typed.** A document half typed does not parse, and the overview starts afresh
  when its document changes, losing what the reader had opened. A save is a moment the author chose. The unsaved text is
  one click away for whoever wants it at once.
- **Not done: rendering rows with the unpushed text.** `sqlflow preview` and `sqlflow values` already do it on the
  working copy, and need the catalog and the flow's source connection on the developer's machine, which a page cannot
  assume. They stay the command line's.

## What a webview cannot do

A webview may start a worker only from what the page already holds, never from a file of the extension's folder. The
code editor's two workers are therefore built into the script that starts them. The GUI's own language engine, a worker
that loads a WebAssembly file, is left unstarted in a hosted page: the document being written is in the editor itself,
where the language server analyses it, and a page's code views are read-only.

## One package per platform

A package carries three programs built for the platform it installs on, so the Marketplace holds one package per
platform and gives each editor its own. A single package holding every platform's programs would be several hundred
megabytes, most of it never run. Each is built on a runner of its platform by the packager a developer runs for their own
machine, and publishing is a box the person ticks: a run without it only keeps the packages.

## Known limits

- **A page is built for the API of the release it ships with.** An extension updates on its own schedule, and a control
  plane on its estate's. A call a newer page makes of an older control plane is answered by the API's own error, which
  the page shows; nothing checks the two versions against each other yet, because the control plane does not say its
  version.
- **The run form is the editor's own prompts.** Starting a run asks its questions in the editor's quick picks, as the
  first version did, and only then opens the run's page.
