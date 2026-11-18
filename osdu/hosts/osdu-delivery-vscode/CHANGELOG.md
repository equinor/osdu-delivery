# Change log

What changed in the OSDU Delivery extension for VS Code, newest first. The extension has a version of its own; the
product's own changes are in the [repository's change log](https://github.com/TahirRiaz/osdu-delivery/blob/main/CHANGELOG.md).

## [1.0.4]

### Restricted until complete

- A mapping completes and explains `restrictedAccess` in its header, and a property's hover describes
  `$restrictWhenMissing` beside `$required`: a record whose marked value is missing goes out under the registered access
  restriction `restrictedAccess` names, and gets its own access back once the value arrives
  ([restricted until complete](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/flow/mapping-restricted-access.md)).
- A flow's card counts its **Restricted** records after its status counts, restricted until complete or by an
  operator, each once, with each kind on hover, and Home counts them beside Drifted.
- A flow's Records tab and the Records page show an **Access** column, and an **Access** filter beside the status filter
  lists the records restricted either way, restricted until complete, or restricted by an operator; each record
  restricted until complete says on hover what it goes without, and its page names the restriction and links to every
  record missing the same values. The mapping builder's switch marks a node, and **Check values** counts its rows as
  restricted.

## [1.0.3]

### Signing in

- Before you sign in, the side bar is the way in: the product and what it is for, the control plane you sign in to with
  whether it answers, and **Sign in**. While your browser is open it shows the code to approve, with **Open the page
  again** and **Cancel** (**Cancel the Sign-In**), and it turns into the menu once you approve.
- The browser's page that approves the code carries the product's brand, as its login page does, with the account the
  editor will act as and what it may do.
- The side bar's search, account and home buttons appear once you are signed in, when they have something to open.

### The command line

- Signing in, or switching the control plane in your settings, makes it the command line's current control plane, so
  `sqlflow` reaches it from any shell or script with no `--url`, however many control planes you are signed in to.
  `sqlflow use <url>` switches it, and `sqlflow use` alone says which one a command reaches and why.

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
