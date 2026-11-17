# Change log

What changed in the OSDU Delivery extension for VS Code, newest first. The extension has a version of its own; the
product's own changes are in the [repository's change log](https://github.com/TahirRiaz/osdu-delivery/blob/main/CHANGELOG.md).

## [1.0.2]

### Your other tools

- Signing in puts the `sqlflow` command line and the MCP server in `~/.sqlflow/bin`, at the end of your own PATH, and
  registers the server with Claude Code for all its sessions, so Copilot and Claude Code sessions, terminals of your own
  and scripts have them, signed in as you. It says what it changed, with **Undo**; **Make the Command Line and Server
  Available to Other Tools** and **Remove the Command Line and Server from Other Tools** do and undo it
  ([your other tools](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/guides/vscode.md#your-other-tools)).
- **Connect an AI Assistant** registers Claude Code the same way, where it put a command in a terminal for you to run.
- The server's `get_environment` tells an assistant how to call the command line where it runs, what its verbs do, and
  what a sandboxed shell has to be allowed; the chat's servers run from `~/.sqlflow/bin`, so an assistant is never sent
  into the extension's own folder, whose name changes with every version.
- A chat's server takes the editor's sign-in for the control plane the editor uses, where one started before it was named
  said no one was signed in.

### The command line

- `sqlflow --help`, `sqlflow -h` and `sqlflow help` print the usage and succeed, where they ended with an error unless a
  whole command came with them.

## [1.0.1]

### Reports

- A report (`flowType: rpt`), read-only queries over the data shown as figures, charts and tables, is understood as it
  is typed: completion, hover and problems for its keys. New Document starts one
  ([writing a report](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/guides/write-a-report.md)).
- The side bar's menu lists the Reports page under Explore, and **Open Reports** opens it. A report opens on its own
  page in a tab, which runs its queries when it is opened and draws their answers, narrowed by the report's filters or
  by a click on a bar.
- A report document carries a **Report Page** lens that opens its report beside the file, and **Open Flow Page** on one
  opens the report as well. A report has no run, so it carries no last-run lens.

### The Marketplace page

- A shorter page: what OSDU Delivery does, why it pays off, how to start, and links to its open-source repositories.

## [1.0.0]

The first release.

### Writing documents

- Completion, hover and problems for every key of every document kind, from the language engine the control plane
  validates with. A YAML file is recognised as a pipeline document by what it holds.
- A mapping completes the properties of its template, says what the schema says on hover, and reports a key the
  template lacks or a required property nothing fills, from the template folders of the working copy.
- Links from a flow to the mapping it pins and the schedule it joins, and from a mapping to its template.
- New Document, New Mapping from a Template, Fetch a Template into This Repository, Validate Repository, and Compare
  with Synced.

### The estate in the editor

- One drawn side bar, the web GUI's own navigation: its search, its menu, and a button that syncs a repository on the
  control plane signed in to, after a confirmation for one that holds its commit.
- Every page of the web GUI opens in a tab of the editor, in the colours of the editor's theme, and comes back after a
  restart on the page it showed.
- Preview Mapping opens a mapping's page beside its file and reads the file: its overview, its staged records with the
  documents they render to, and the properties of its template. It follows the file as it is saved.
- A flow runs from its page, with the GUI's own run form, and the run's page shows its trace as it is written.

### Sign-in, the assistant and the command line

- Sign in through the browser, Microsoft single sign-on included. The editor, its chat, the assistants you connect and
  the `sqlflow` command line share one sign-in.
- The OSDU Delivery MCP server is registered with the editor's chat, and Connect an AI Assistant registers it with
  Claude Code, Cursor or Codex.
- The `sqlflow` command line is on the path of every integrated terminal.
