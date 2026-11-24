# Change log

What changed in the OSDU Delivery extension for VS Code, newest first. The extension has a version of its own; the
product's own changes are in the [repository's change log](https://github.com/TahirRiaz/osdu-delivery/blob/main/CHANGELOG.md).

## [1.0.6]

### Data streams

- **A data stream has a page of its own.** A row on the Data streams board, or **Open data stream** in the lineage graph,
  opens the stream followed end to end: where its data enters, the `pre` table, the ingestion flow and what it keys into,
  and every flow after it, with a strip of fires to pick one from and each card saying what happened in that fire (the
  files that arrived, what each run took in and changed, what a delivery delivered, where the data stopped). A node opens
  on the right with its run in the fire and its verdict over the window
  ([following one data stream](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/guides/follow-a-data-stream.md)).

### Saving an environment

- Saving an environment in a window that opened before the extension was installed or updated said only that
  `sqlflow.environments` "is not a registered configuration". The side bar now says the window has not loaded the
  setting, and the notification offers **Reload Window**, after which the save goes through
  ([the setting](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/guides/vscode-environments.md#the-setting)).

### Delivery

- **The Delivery page filters its flows.** The field first in its header narrows the flow cards to the flows whose card
  shows every word typed (a name, a path, a batch, a partition, or a verdict such as failed or held), says how many
  match, and keeps the partition's totals. It used to look a record up on the Records page instead, which **Find a
  Record** and the Home page still do
  ([the Delivery page](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/concepts/gui-ledger.md#delivery)).

### Dimensions

- **A dimension's Table tab says how fresh each row is.** Two columns, **Inserted** and **Updated**, show when a build
  first wrote the row and when one last changed it (the table's `InsertedDate_DW` and `UpdatedDate_DW`), with the UTC
  time on hover, and the rows can be ordered by either, the latest first. The editor's help on a dimension's
  `attributes`, `columns` and a view's columns names the two, which no attribute or key or value column can take
  ([the dimension's table](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/flow/dimension-table.md)).

### The command line

- **`sqlflow records release` names the records it releases.** It takes `--key`, `--issue` or `--all`, and with none of
  them stops and says so, where it used to release every blocked record of the flow. The control plane the pages and the
  MCP server talk to refuses a release, redelivery, bringing up to date or deletion from the ledger that names no
  records in the same way; **Release blocked** and **Redeliver** on the pages, and the MCP server's `allHeld`, say which
  records they mean and work as before
  ([records release](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/cli/records.md#records-release)).
- **`sqlflow records adopt --key` takes `--confirm`.** Adopting records named by key takes the partition typed back, as
  adopting every record or an issue's already did and as the pages' **Adopt** in the Records tab's selection bar does;
  without it nothing is asked
  ([records adopt](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/cli/records.md#records-adopt)).

## [1.0.5]

### Environments

- **Dev, test and prod, each in its colour.** Save each control plane you work against with a name and a stage
  (development, test, production), and switch between them from the menu at the top of the side bar. The sign-in kept
  for each comes with it, so an environment you signed in to opens with no browser while its session lasts; the list
  says which hold one. Switching to production asks first.
- **Always know where you are.** The environment in use is a line and a name in green, orange or red across the side
  bar, on every page's strip (production on red) and in the status bar (production on the editor's red).
- **Adding one is a form, not an input box**: the address is checked as you type, the name comes from it, and the stage
  is picked when the name says one (and asked for when it does not). The sign-in's card shows the environment you sign
  in to, and **Sign in to** names it. **Switch or Add an Environment** replaces **Set Control Plane URL**, and the list
  is the `sqlflow.environments` setting, in your own settings only.
- Moving to another control plane closes the pages open from the one before, whose addresses name its own flows, runs
  and records, and **Open in the browser** follows the environment's GUI address
  ([environments in the extension](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/guides/vscode-environments.md)).
- The Home page has one scroll bar, where it showed a second beside its own.

### A run you start

- **Delete ledger**, **Redeliver**, **Sync timelines**, **Release blocked**, **Adopt**, **Remove**, a reversal and the
  other actions that queue a run open the run's live trace in the page's bottom panel, with **Open run** to its page,
  and keep it behind the trace indicator once the panel is closed. A delivery flow's **Delivery**, **Records** and
  **Submissions** tabs name a run of the flow while it is queued or running, with how long it has run, what it is doing,
  **Trace** and **Open run**
  ([a run an action starts](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/concepts/gui.md#a-run-an-action-starts)).

### A record set aside, not held

- A record whose queued document was not where the run sending it reads its work from (another node wrote it, or it was
  pruned) shows **Set aside: its queued document could not be read where it was sent from** on its timeline, and **Set
  aside: waiting for the next run** until a run sends it, rather than **Held** with "Not tried again until someone
  releases it": nothing about it is wrong and nobody has to release it
  ([work queued where a run cannot read it](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/concepts/submissions.md#work-queued-where-a-run-cannot-read-it)).

### Why a record was delivered

- A record's timeline titles each **Delivered** row with what sent it, and opens to every input that moved (the mapping
  with its fingerprint and estate commit on each side, the source row, the cache, a request and who made it, ...) and the
  paths of the document that changed; a delivery made before says its reason was not recorded. A submission's page counts
  its deliveries by what sent them, and the MCP server the extension carries returns the reason in `delivery_record` and
  the counts in `delivery_submissions`
  ([delivery reasons](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/concepts/delivery-reasons.md)).
- Each cause in a submission's counts, a run's **Delivered for** and the hover over a run's **Delivered** count on a
  flow's **Runs** tab opens the records sent for it, under a **Delivered for** chip on the Records tab. A record's header
  says why its last delivery was sent, and a **Nothing to send** entry says what moved when a mapping, the cache or a
  request reached a record without changing it. The MCP server's `delivery_run` counts a run's deliveries by cause, and
  `delivery_flow_records` takes `trigger`.

### A run's records

- A delivery flow's **Runs** tab has a **Records** icon on each run that opens the flow's Records tab narrowed to the
  records the run touched, those of the parts a delivery split over nodes included, and **Records of this run** on a
  run's page opens them in the interface and partition the run worked in.
- What a link narrowed a flow's Records tab to (a run, a submission, an issue, a set of missing values) is a chip at the
  top of the list whose id opens what it names and whose cross drops it, with **Clear all** for several. The run's chip
  says its operation, how it ended and when, and leads from a part to the whole run
  ([a delivery flow's page](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/concepts/gui.md#a-delivery-flows-page)).
- A delivery flow's **Runs** tab shows what each run **Delivered**, found **Unchanged**, **Held** and **Failed**, in
  place of the row counts (Rows loaded, Inserted, Updated, Deleted, Files) a delivery run never reported.

### A field that contains another

- A mapping property's hover describes the `$findAll` line `<field> contains <field>`, which keeps the cached rows whose
  text under one field holds their text under another, and the mapping builder's find all adds such a line under the
  fields that must be empty
  ([$findAll](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/flow/mapping-lookups-find-all.md)).

### Mappings read more strictly, and kept current

- A mapping property's hover says that a `$findBy` text reads `{$param.<name>}` and that the first line finding exactly
  one record wins. The command line and the MCP server the extension carries refuse a modifier text YAML would read
  back otherwise (an unquoted `01` or `1.0`), naming its line, and give the fix for an empty modifier setting, a stray
  `otherwise` and an unquoted `{$param.<name>}`
  ([rules that hold for every modifier](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/flow/mapping-modifiers.md#rules-that-hold-for-every-modifier)).

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

### A mapping's overview

- The YAML beside a mapping's overview, in **Preview** or on its page, closes from the button at the end of its heading,
  as **YAML** on the toolbar closes it.
- A part's menu leaves out **Details**: **Show in the YAML** shows the lines of the same part beside the canvas, and a
  double click, or Enter, still opens its details
  ([a mapping's overview](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/concepts/mapping-overview.md#the-menus)).

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
