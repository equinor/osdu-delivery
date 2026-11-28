# Change log

What changed in the OSDU Delivery extension for VS Code, newest first. The extension has a version of its own; the
product's own changes are in the [repository's change log](https://github.com/TahirRiaz/osdu-delivery/blob/main/CHANGELOG.md).

## [1.0.8]

### Templates

- **Comparing two versions of a kind says which changes matter to your mappings.** Compare versions weighs every change
  against what the synced mappings of the kind write: the significant ones (what a mapping has to change, or what it
  writes that the newer version deprecates) come first, the minor ones after, and the changes no mapping uses are folded
  away. Its **Report** also saves as an HTML page to read, print or send, and says for each mapping what the move means,
  line by line of its document ([comparing template
  versions](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/concepts/template-versions.md)).

### Lineage

- **A trace's side and reach are glyphs on the canvas.** While a node is traced, the canvas's control in its lower left
  corner holds three arrows over the zoom, **Upstream**, **Both sides** and **Downstream**, each pointing the way that
  side lies in the drawing, and the reach as one glyph showing how many hops are drawn, which opens **1 hop**, **2
  hops**, **3 hops** and **All, to the ends**. Each says what it does on hover. The line under the bar names the traced
  node and the way out, and no longer spells out the sides and the hops ([reading the lineage
  graph](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/guides/lineage-graph.md#trace)).

## [1.0.7]

### What a drop zone's files carry

- **`source_columns` reads the drop zone a pre flow's own YAML names.** The scan runs on a node from the flow's stored
  YAML, with the values the flow's runs are supplied with, so a location such as `${env:LANDING_URL}/wells` resolves as
  it does in a run. In 1.0.6 it was read by the control plane, which does not hold those values, and failed as an unset
  variable. The tool takes a `pool` for a flow whose runs run in a pool of their own
  ([scanning a flow's source](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/concepts/file-discovery-and-lifecycle.md#scanning-a-flows-source)).

## [1.0.6]

### Templates

- **A report on moving to another version of a kind.** Compare versions on the Templates page has a **Report** that
  copies as Markdown or saves as Markdown or JSON what moving from one version to the other takes: what breaks, what each
  synced mapping of the kind has to change, and what a mapping may now use. The MCP server's
  `delivery_osdu_schema_compare` returns the same report ([comparing template
  versions](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/concepts/template-versions.md#the-report)).

### Lineage

- **A mapping is drawn as its delivery's input.** On the lineage graph a mapping sits right above the delivery
  rendering with it, in that delivery's wave, even when it reads what an earlier wave delivered, and a trace draws every
  delivery it draws with its mappings ([the mapping in
  lineage](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/concepts/lineage.md#the-mapping-is-a-node-between-the-cache-and-the-flow)).
- **The lineage graph's bar is one row.** The bar holds the search, the scope, how much is drawn and **What moved**.
  **View** holds how the drawing is made (the wave range, kinds, folders and stacks, described steps, the layout), and
  **More** holds Download as SVG and the object explorer. The line under the bar counts what is drawn, holds a trace's
  controls while a node is traced, and names only the reductions made. Nothing stands on the canvas but the drawing and
  one control in its lower left corner (the zoom buttons, under a map of the drawing when it is large), and a node's
  details dock to the canvas's right edge ([reading the lineage
  graph](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/guides/lineage-graph.md#the-bar)).
- **The lineage graph shows what moved.** **What moved** in the graph's bar makes every card say what its node did in
  the day looked at (the files that arrived, the rows a flow read and changed, what landed in a table, the records a
  delivery flow delivered) and chart it over the window, and inks and weighs every line by what it carried, so a
  delivery is followed from where it enters to where it stopped. The line under the bar names the day looked at,
  between the arrows that step it, and nothing stands under the canvas. A selected node's panel holds that node's own
  days, each with what it moved, and its runs that day. A row on the Data streams board opens its flow's trace this way
  ([following data on the lineage
  graph](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/guides/follow-a-data-stream.md)).

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

### Runs

- **A run that succeeded with warnings shows it.** Wherever a run's status shows, one that succeeded with warnings wears
  the warning tone and an alert, and its label says how many; a pipeline's **Last run** says the count beside the
  duration, and `sqlflow runs show` prints it after the status
  ([column type history](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/concepts/column-type-history.md#the-gui)).
- **A run's page lists its column type changes.** A **Type changes** card lists what the run did about each change to an
  existing column's type: the column, from which type to which, the tier and the outcome, and on opening a row why, the
  values that refused it and the steps to make it by hand. A pipeline's page shows the ones its latest run left for an
  operator until a run no longer reports them
  ([column type history](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/concepts/column-type-history.md#the-gui)).

### Pages left open

- **A page behind another tab asks for nothing.** A page you keep open behind another tab, and the side bar while it is
  closed, no longer polls the control plane: it reads what went stale as soon as it is in front again. Several pages left
  open no longer add up past the calls a minute a person is allowed
  ([the side bar and the pages](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/guides/vscode.md#the-side-bar-and-the-pages)).
- **A page refused for calling too often comes back by itself.** A page the control plane refused with a 429 waits as
  long as it is told, then reads again what was refused, where it used to show "HTTP 429" until it was closed. The
  Delivery page reads every flow's counts in one call, and a flow whose document does not parse says **Not counted** and
  why on its card ([rate
  limiting](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/concepts/control-plane.md#health-probes-and-rate-limiting)).

### Dimensions

- **A dimension's Table tab says how fresh each row is.** Two columns, **Inserted** and **Updated**, show when a build
  first wrote the row and when one last changed it (the table's `InsertedDate_DW` and `UpdatedDate_DW`), with the UTC
  time on hover, and the rows can be ordered by either, the latest first. The editor's help on a dimension's
  `attributes`, `columns` and a view's columns names the two, which no attribute or key or value column can take
  ([the dimension's table](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/flow/dimension-table.md)).

### The command line

- **`sqlflow plan` plans an ingestion flow.** It reads the live source and target and prints what the next run would do:
  the columns it adds, and every change to an existing column's type with its tier, what it costs and its verdict (planned,
  pending an opt-in, refused with the values that do not convert, or manual with the steps). It changes nothing, and
  exits 2 when a change needs attention and 1 when the load is blocked ([plan](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/cli/plan.md)).
- **`schema.convertTypes` in an ingestion flow.** The editor knows the new key, which lets a run convert an existing
  column to the type its source now declares after checking every stored value, and a pre flow's typed view is rebuilt
  on every run, also one that finds no new file ([column type changes](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/concepts/column-type-changes.md)).
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

### What changed in a table, and the type a column should have

- **Schema changes covers every table a flow reaches.** Each sync records a table or view whose columns it read
  differently from the time before, and **Schema changes** lists the columns that moved under each change (added,
  dropped, or changed in type, nullability or place), finds a change by a column's name, and sets the table's columns
  before the window against now. No snapshot flow is needed
  ([schema history](https://github.com/TahirRiaz/osdu-delivery/blob/main/sqlflow/docs/reference/concepts/schema-history.md)).
- **The MCP server answers three new questions.** `source_columns` reads the headers of the newest files at a pre
  flow's source and sets their columns against the table it loads into; `infer_column_types` counts what a table's
  column values convert to; `delivery_column_types` says, for each column a delivery flow's mapping reads, the type it
  should have for the OSDU property it fills, and why
  ([column types](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/concepts/column-types.md)).

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
