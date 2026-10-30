---
id: delivery-wiki-0039-vscode-extension-is-an-mcp-client
title: "Decision 0039: the VS Code extension is a client of the MCP server, and one browser sign-in serves it and the command line"
type: decision
summary: "Why the extension reads through the MCP server, why one credential file serves it and the CLI, and why a browser sign-in goes through the GUI."
keywords:
  - vs code extension
  - mcp client
  - single sign-on
  - microsoft entra
  - device grant
  - device approval page
  - template folders
  - extension host
  - credentials.json
  - sqlflow login
  - get_cli
updated: 2026-10-30
sourceRefs:
  - sqlflow/tools/sqlflow-vscode/src/host.ts
  - sqlflow/tools/sqlflow-vscode/src/mcp/mcpServer.ts
  - sqlflow/tools/sqlflow-vscode/src/mcp/signIn.ts
  - sqlflow/tools/sqlflow-vscode/src/cli.ts
  - sqlflow/tools/sqlflow-mcp/src/config.rs
  - sqlflow/src/SqlFlow.Cli/Remote/CredentialStore.cs
  - sqlflow/gui/src/auth/DeviceApprovalPage.tsx
  - sqlflow/src/SqlFlow.ControlPlane/Security/DeviceApprovalPage.cs
  - osdu/hosts/osdu-delivery-vscode/src/extension.ts
  - osdu/hosts/osdu-delivery-vscode/src/templates.ts
referenceRefs:
  - delivery-guide-vscode
  - delivery-guide-mcp
  - delivery-concept-authentication-and-identity
---
# 0039: The VS Code extension is a client of the MCP server, and one browser sign-in serves it and the command line

Status: accepted. Documented in [the VS Code extension](../../reference/guides/vscode.md).

The human's words: "The goal for this extion is to provide all required information to develop pipelines. Note that the
combination with MCP should be built in.", "I want the vscode to be the primary tool for yaml file development. It should
have access all relevant infomration.", "I want Singel sign on option where we authenticate with the browser", "azure ms
signon is the main feature", and "as a part of this vs-code extention we should also deliver sqlflow-binary. The idea being
that things can be scripted by the agent, rather than the user clicking around.", "note that extention and sqlflow cli must
be able to use the same authentication." The extension had not moved since SQLFlow was vendored: it keyed on `*.flow.yaml`, which
this product's repositories do not use, so it never activated on one, and it knew nothing of the module's kinds.

## The extension reads through the server

Every view, panel and command reads through the MCP server's tools, and the extension holds no REST client.

- **One estate, one set of rules.** The assistant beside the code and the panel it answers about read the same answers,
  under the same rule of what is metadata ([the server's own decision](../../../../sqlflow/docs/wiki/decisions/the-mcp-server-is-the-assistants-one-definition.md)).
- **A missing tool is a gap in the server, and is filled there.** The first pass over what a developer needs found seven:
  a run's trace, which `/trace/text` had been built for and no tool read; firing a schedule and syncing a repository after
  a push, which the old extension did over REST; the datasources a `connection` can name; the kinds, their run operations
  and the keys of a document that is not a flow (the key tools took only a flow kind, though the guide said otherwise); a
  flow's issues, inventories and removals; and a dimension's blueprint. An assistant gains each as the extension does.
- **Rejected: a REST client for what the tools withhold.** A record's rendered content, what OSDU holds and a dimension's
  values stay out of the views. The extension signs in through the server, so a second path would need a second
  credential. `sqlflow preview`, `check` and `values` answer those on the working copy, which is what a developer wants
  from them anyway, and the GUI answers for what is synced.

## One sign-in, in the browser, through the GUI

The server already kept a session and renewed it, so the extension uses it: signing in is the server's `login`, and an
assistant registered with the same program shares the session. What it did not have was Microsoft. Its approval page,
`GET /device`, takes a username and a password, and an account that signs in with Entra has neither, so a device grant
could not reach one, in the extension or in any assistant.

The GUI has Entra: its origin is the redirect URI an app registration names, and `POST /auth/device/approve` already takes
any signed-in session and carries over its `auth_time`, so the client's session keeps the approving person's limit. So the
GUI got a `/device` page behind its sign-in, and the control plane hands `GET /device` to it when it knows where the GUI
is, from `ControlPlane:Notifications:GuiBaseUrl` or the one CORS origin a deployment allows. Every client of the device
grant gains single sign-on, the MCP server's `login` in Claude Code included, with nothing more to register.

- **Rejected: VS Code's built-in Microsoft account provider.** One click where the account is already in the editor, but
  it needs the app registration to take public-client redirect URIs a deployment does not have today, which an estate's
  directory owners may not grant, and it would serve this one client alone. It reads an ID token VS Code does not document
  as part of its API.
- **Rejected: a page of the control plane's own that signs in with Entra.** Its origin would need registering as a second
  redirect URI, on every deployment.
- **Rejected: approving by opening the link.** One click fewer, and anyone who sends a link could then sign in as whoever
  opens it, which is device-code phishing. Approving takes a click on a code the person can compare.

## One credential, for the extension, the server and the command line

A person signed in separately to `sqlflow` and to each MCP server, in separate files. So one file holds it:
`~/.sqlflow/credentials.json`, which `sqlflow login` already kept per control-plane URL. The server reads and writes it, and
the command line reads what the server left there. Keyed by URL, it keeps what the server's prefixed files were for: two
control planes never read each other's credential.

- **A server takes up a sign-in on its next call,** by checking the file's time before a request, so `sqlflow login` signs in
  an editor that is already open with no restart, and a sign-out anywhere reaches everywhere.
- **The file holds a session as well as a token.** It held personal access tokens alone, and the server deliberately mints
  no long-lived credential from a browser approval, so the file gained what makes a session renew and the command line
  renews one on the schedule the GUI and the server keep. Otherwise the command line would lapse within the hour of an
  editor's sign-in. `sqlflow login` still stores the personal access token it always did.
- **`sqlflow login` opens the browser.** It asked for a password before, which an Entra account does not have. `--device`
  prints the link, for a machine with no browser, and `--username` is the local accounts' path.
- **Rejected: passing the token to the command line in the environment.** It would serve the terminals the extension starts
  and nothing else, and a person's own shell or an assistant's would sign in again.
- **Rejected: the extension keeping the credential in VS Code's secret storage.** Only the editor could read it, which is the
  thing being fixed. The file is owner-only, as `sqlflow login`'s always was.

## The command line ships with the extension

The package holds three programs: the language server, the MCP server and `sqlflow`, a single file for its platform, about
118 MB of the package's size. The tools say what the estate is and the command line lets an assistant act on a working copy by
script, in a loop, without a person clicking: every integrated terminal has it on its PATH, and `get_cli` tells an assistant
where it is wherever it runs.

The two have different rules on purpose, and the seam is stated rather than hidden. The MCP server answers from metadata
([its rule](../../reference/guides/mcp.md#metadata-and-a--questions-query)). The command line is the person's own, on their
machine, and `preview` and `values` print row values, so an assistant that runs them sends those values to its model
provider. Nothing can stop that from here, so `get_cli` and the instructions tell the assistant to run those two, and any verb
that writes, only when the person asked.

## The extension is a host, as the server and the GUI are

`sqlflow/tools/sqlflow-vscode` became a library whose own extension is the host with no modules, and
`osdu/hosts/osdu-delivery-vscode` composes it with the delivery module, compiling its sources in place as the GUI does.
A module adds sections to the Document view, references, lenses, commands and a view; the host names the program, the
census, the documentation and the skeletons. VS Code reads a manifest as a file, so this one restates SQLFlow's
contributions, and a suite holds the two together.

## Mappings read the working copy

A mapping's completion, hover and findings read the template folders on its path from disk rather than asking the control
plane, as `sqlflow template folders` does: the template a mapping is being written against is often not pushed yet, and a
person types faster than a round trip. The control plane's check stays the final word, and the findings as you type say
only what is certain: a key the template does not hold, and a required property nothing fills.

- **Rejected: the language server knowing templates.** It is SQLFlow's and sees one file; a template is a module's, and
  reading a folder is not what a language engine shared with the GUI's WebAssembly build can do.
