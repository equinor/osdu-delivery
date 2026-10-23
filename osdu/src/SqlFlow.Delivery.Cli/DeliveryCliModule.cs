using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Delivery.Telemetry;
using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Hosting;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The OSDU module as the <c>sqlflow</c> CLI composes it: the delivery, retrieval, cache, assertion, dimension, inventory
/// and removal flow kinds (so SQLFlow's own <c>validate</c>, <c>run</c> and <c>worker</c> verbs read and execute them),
/// the ledger, templates and caches over the module's database, and the module's own verbs: <c>check</c>,
/// <c>preview</c>, <c>values</c>, <c>records</c>, <c>config</c>, <c>partition</c>, <c>cache</c>, <c>template</c>,
/// <c>assertions</c>, <c>dimensions</c>, <c>inventory</c>, <c>removal</c> and <c>dev</c>, which sets a checkout up for a local run.
/// </summary>
/// <remarks>
/// The module's database is the catalog's, which holds the <c>osdu</c> schema: a command opens it on the catalog the command
/// line names (<c>--db</c>, else <c>${env:SQLFLOW_CATALOG_DB}</c>), and a worker node, which SQLFlow gives no catalog
/// connection, through <c>${env:SQLFLOW_CATALOG_DB}</c>, and says so when it is unset.
/// </remarks>
public sealed class DeliveryCliModule : ICliModule
{
    /// <summary>The module's name: what the CLI calls it in errors, and the name its database is registered under.</summary>
    public string Name => OsduSchema.Module;

    public IReadOnlyList<CliVerb> Verbs =>
    [
        new CliVerb(
            "check",
            [
                "sqlflow check    <flow.yaml> [--interface <name>] [--partition <id>] [--set k=v]... [--connect]",
                "                                   The delivery preflight: the flow's documents, its mapping against the",
                "                                   template it fills, read from the template folders on the mapping's path,",
                "                                   and the version of the cache it reads (needs --db when the mapping reads",
                "                                   a cache: caches live in the module's database). With --connect it",
                "                                   also opens the flow's ingestion tables as a run opens them, checks every",
                "                                   column the mapping reads against them, and reports what the next run",
                "                                   would read and why.",
                "                                   A flow that declares interfaces is checked one interface at a time, each",
                "                                   with its route, its ledger and what it waits for; --interface checks one.",
                "                                   A flow that names its partitions is checked in the one --partition names.",
            ],
            DeliveryVerbs.CheckAsync)
        {
            Flags = ["--connect"],
            ValueOptions = ["--interface", "--partition"],
        },
        new CliVerb(
            "preview",
            [
                "sqlflow preview  <flow.yaml> [--interface <name>] [--partition <id>] [--key <key>] [--set k=v]... [--out <file.json>]",
                "                                   Render one record as a delivery would, and send nothing: the scope's",
                "                                   first record, or the one --key names (a delivery key, an OSDU id the",
                "                                   ledger holds, a source key, or a JSON array of the key's parts). Says what",
                "                                   the next run would do with it, and shows the document, the files it would",
                "                                   upload and the requests the route would make (needs --db). The console",
                string.Create(CultureInfo.InvariantCulture, $"                                   leaves out a document over {RecordPreviewLimits.Default.MaxDocumentChars:N0} characters, as the GUI does; --out"),
                "                                   writes the whole preview as JSON, its document whole however large.",
            ],
            DeliveryPreviewVerbs.PreviewAsync)
        {
            ValueOptions = ["--interface", "--partition", "--key", "--out"],
        },
        new CliVerb(
            "values",
            [
                "sqlflow values   <flow.yaml> [--interface <name>] [--partition <id>] [--target <osdu.path>]... [--set k=v]...",
                "                 [--max-rows <n>] [--samples <n>] [--skip <n>] [--rows <file.csv>] [--out <file.json>]",
                "                                   Find the rows that will not give the mapping's variables the values the",
                "                                   template expects: every row of the scope rendered as a delivery renders it,",
                "                                   and every value held to the template's rules. Says, variable by variable,",
                "                                   how many rows hold the record, write a value the template does not accept,",
                "                                   or leave the variable out, with each reason, the values behind it and",
                "                                   example records (needs --db). --target checks one variable and what is",
                "                                   inside it (repeat it for more); --max-rows reads that many rows (10,000 by",
                "                                   default, 0 for the whole scope); --samples names that many examples per",
                "                                   reason after the first --skip; --rows writes every failing row to CSV;",
                "                                   --out writes the whole check as JSON. Exits 1 when a row is held or",
                "                                   writes a value the template does not accept. Nothing is written elsewhere.",
            ],
            DeliveryValueCheckVerbs.ValuesAsync)
        {
            ValueOptions = ["--interface", "--partition", "--target", "--max-rows", "--samples", "--skip", "--rows", "--out"],
        },
        new CliVerb(
            "records",
            [
                "sqlflow records list <flow.yaml> [--interface <name>] [--partition <id>] [--search <term>] [--contains]",
                "                     [--status <status>] [--issue <id>] [--max <n>]",
                "                                   An interface's records from the ledger: the key, the status, the OSDU id",
                "                                   and the last error of each (needs --db). A flow that names its partitions",
                "                                   keeps a ledger per partition: --partition names the one to read",
                "sqlflow records show <flow.yaml> --key <delivery key | source key> [--interface <name>] [--partition <id>]",
                "                     [--attempts <n>]",
                "                                   One record with every try it took: what each sent, what the target",
                "                                   answered step by step, and why it stopped (needs --db)",
                "sqlflow records issues <flow.yaml> [--interface <name>] [--partition <id>] [--issue <id>] [--max <n>]",
                "                                   The issues keeping an interface's records blocked, the most records",
                "                                   first: each with its counts, the error its records share and an",
                "                                   example; --issue shows one with its files and samples (needs --db)",
                "sqlflow records release <flow.yaml> [--interface <name>] [--partition <id>] [--key <delivery key>]...",
                "                        [--issue <id>]",
                "                                   Release blocked records (held, failed, deleted or reverted) back to",
                "                                   pending, all of them, the ones named, or every one an issue keeps",
                "                                   blocked, once their cause is fixed (needs --db)",
                "sqlflow records adopt <flow.yaml> [--key <delivery key>... | --issue <id>] [--confirm <partition>]",
                "                      [--interface <name>] [--partition <id>]",
                "                                   Adopt the records OSDU already holds at the ids held records' keys",
                "                                   make, written by another system or another instance: every such",
                "                                   record, an issue's, or the ones named. The next run takes each over",
                "                                   and writes its row there. All or an issue's take the partition typed",
                "                                   back with --confirm (needs --db)",
                "sqlflow records reverse <flow.yaml> (--run <id> | --submission <id>) [--interface <name>] [--partition <id>]",
                "                        [--preview]",
                "                                   Put OSDU back as it was before one run or submission: what it created",
                "                                   is removed again (reversibly), what it updated gets back the version",
                "                                   OSDU held before, and those records stay blocked until their source",
                "                                   changes or they are released. --preview says what it would reach and",
                "                                   writes nothing; asking again resumes a reversal that stopped (needs --db)",
                "sqlflow records reversals <flow.yaml> [--interface <name>] [--partition <id>] [--run <id> | --submission <id>]",
                "                          [--outcome <outcome>] [--max <n>]",
                "                                   The reversals the ledger keeps, newest first; or one, with its records",
                "                                   counted by outcome, and with --outcome the records it settled so (needs --db)",
                "sqlflow records artifacts <flow.yaml> --key <delivery key | source key> [--interface <name>] [--partition <id>]",
                "                          [--max <n>]",
                "                                   What deliveries of one record created in OSDU, newest first: each",
                "                                   dataset, session, version and the like with its OSDU id or what finds",
                "                                   it, its versions, where it stands and who settled it (needs --db)",
                "sqlflow records undos <flow.yaml> [--interface <name>] [--partition <id>] [--max <n>]",
                "                                   What unfinished deliveries left in OSDU that an undo may still take,",
                "                                   counted by state, and the records holding it, those whose undo used",
                "                                   every try first. The undo is a run: sqlflow run <flow.yaml> --operation",
                "                                   undo (with --payload '{\"force\":true}' it retries those too) (needs --db)",
                "sqlflow records removed <flow.yaml> [--interface <name>] [--partition <id>] [--state <state>] [--after <key>]",
                "                        [--max <n>]",
                "                                   The ledger's deletions: the keys a removal flow found its source no",
                "                                   longer holds, in key order, each with why, as of when, and what taking",
                "                                   it out came to; --state pending, awaiting-approval or removed shows one",
                "                                   state, and the line under the page names the --after of the next (needs --db)",
                "sqlflow records lift-removal <flow.yaml> --key <delivery key>... [--interface <name>] [--partition <id>]",
                "                                   Lift the deletion of the records named: their keys are no longer kept",
                "                                   out, and the flow's next run delivers each again under its id (needs --db)",
            ],
            DeliveryRecordVerbs.RecordsAsync)
        {
            Subcommands = ["list", "show", "issues", "release", "adopt", "reverse", "reversals", "artifacts", "undos", "removed", "lift-removal"],
            Flags = ["--contains", "--preview"],
            ValueOptions = ["--interface", "--partition", "--search", "--status", "--max", "--key", "--attempts", "--issue", "--confirm", "--run", "--submission", "--outcome", "--state", "--after"],
        },
        new CliVerb(
            "config",
            [
                "sqlflow config list [--repo <id>] [--partition <id>]",
                "                                   The central configuration: the values the control plane supplies to the",
                "                                   runs it queues, so a flow's ${env:NAME} resolves from one place rather",
                "                                   than from every node. Every scope is listed; --repo narrows it to one",
                "                                   repository's properties, --partition to one partition's, and both to",
                "                                   the repository's in that partition (needs --db)",
                "sqlflow config effective --repo <id> [--partition <id>]",
                "                                   What a run of that repository's flows is given: the control plane's",
                "                                   properties with the repository's own over them, and for a run bound to",
                "                                   --partition, that partition's values over both (needs --db)",
                "sqlflow config set <name> --value <value> [--repo <id>] [--partition <id>] [--description <text>]",
                "                                   Set a property, for every repository or for one the catalog holds, and",
                "                                   for every partition or for one. The value is a non-secret value or a",
                "                                   ${env:...} or ${keyvault:...} reference a node resolves, never a secret;",
                "                                   a description is at most 400 characters (needs --db)",
                "sqlflow config remove <name> [--repo <id>] [--partition <id>]",
                "                                   Remove a property from the scope named, a repository's even after the",
                "                                   repository is gone (needs --db)",
            ],
            DeliveryConfigVerbs.ConfigAsync)
        {
            Subcommands = ["list", "effective", "set", "remove"],
            ValueOptions = ["--repo", "--partition", "--value", "--description"],
        },
        new CliVerb(
            "partition",
            [
                "sqlflow partition list",
                "                                   The partition registry: the OSDU partitions a flow that names none serves,",
                "                                   and the default one a run that names none runs in (needs --db)",
                "sqlflow partition add <name> [--description <text>] [--default]",
                "                                   Register a partition; the first one registered becomes the default. Each",
                "                                   repository describes its registry-driven flows in it at its next sync",
                "sqlflow partition describe <name> --description <text> | default <name> | remove <name>",
                "                                   Say what a partition is for, make it the default, or take it out of the",
                "                                   registry. Removing deletes nothing kept under it; the default is removed",
                "                                   only when it is the last partition (needs --db)",
            ],
            DeliveryPartitionVerbs.PartitionAsync)
        {
            Subcommands = ["list", "add", "describe", "default", "remove"],
            Flags = ["--default"],
            ValueOptions = ["--description"],
        },
        new CliVerb(
            "cache",
            [
                "sqlflow cache list <partition | cache.yaml> [--partition <id>]",
                "                                   The versions of a partition's cache: when each was captured, by which",
                "                                   run, and what it holds; for a cache flow that names its partitions, of",
                "                                   each partition it builds, or the one --partition names (needs --db)",
                "sqlflow cache import <cache.yaml> --from-dir <dir> [--partition <id>]",
                "                                   Write type files as a version of the cache, for offline work (needs --db).",
                "                                   A cache is captured from OSDU by running its cache flow: sqlflow run <cache.yaml>",
                "sqlflow cache prune <partition> [--keep-days <n>] [--dry-run]",
                "                                   Purge the cache's history: prune the records of every version replaced more",
                "                                   than n days ago (0 to 36500; the partition's retention when left out). The",
                "                                   current version, the one it replaced and pinned versions are always kept;",
                "                                   --dry-run says what it would prune and changes nothing (needs --db)",
            ],
            DeliveryVerbs.CacheAsync)
        {
            Subcommands = ["list", "import", "prune"],
            ValueOptions = ["--from-dir", "--partition", "--keep-days"],
            Flags = ["--dry-run"],
        },
        new CliVerb(
            "template",
            [
                "sqlflow template folders [<dir>] [--for <document>]",
                "                                   A repository's template folders, read from disk with no database: every",
                "                                   folder named templates and the template each file holds, or with --for the",
                "                                   folders a mapping or flow reads, nearest first, and what each kind settles to",
                "sqlflow template capture --kind <kind> [--release <tag>] --out <file.json>",
                "                                   Write a kind's schema from the OSDU data definitions (newest release by",
                "                                   default) as a template file, into a template folder of a repository",
                "sqlflow template import <schema.json> --kind <kind> [--release <tag>] --out <file.json>",
                "                                   | import --from-dir <dir> --kind <kind> --out <file.json>",
                "                                   Write a schema file, bundled with the shared schemas it refers to and",
                "                                   naming its kind, as a template file: a template of one's own",
                "                                   A template is a file the repository holds; its next sync records it",
            ],
            DeliveryVerbs.TemplateAsync)
        {
            Subcommands = ["folders", "capture", "import"],
            ValueOptions = ["--release", "--from-dir", "--out", "--for"],
        },
        new CliVerb(
            "assertions",
            [
                "sqlflow assertions list <flow.yaml> [--partition <id>] [--max <n>]",
                "                                   An assertion flow's runs in a partition, newest first: how many of their",
                "                                   tests passed, failed, warned, errored or were skipped (needs --db)",
                "sqlflow assertions status <flow.yaml> [--partition <id>]",
                "                                   Where each test stands: its latest result, or that it has not run, and",
                "                                   whether its definition changed since (needs --db)",
                "sqlflow assertions report <flow.yaml> [--partition <id>] [--run <n>] [--format json|md|html|junit] [--out <file>]",
                "                                   A run's full report (the latest, or the one --run names): every test and",
                "                                   assertion with what it expected, what it found and the records that failed",
                "                                   it. JUnit XML is what a CI server reads (needs --db). The tests themselves",
                "                                   run as any flow runs: sqlflow run <flow.yaml> --payload '{\"tests\":[\"name\"]}'",
            ],
            DeliveryAssertionVerbs.AssertionsAsync)
        {
            Subcommands = ["list", "status", "report"],
            ValueOptions = ["--partition", "--max", "--run", "--format", "--out"],
        },
        new CliVerb(
            "dimensions",
            [
                "sqlflow dimensions list <flow.yaml> [--partition <id>]",
                "                                   A dimension flow's dimensions in a partition: what each holds, when it",
                "                                   was built, its table, and a newer build that failed or is running",
                "                                   (needs --db). Every dimensions subcommand reads the partition",
                "                                   --partition names, or the one a run of the flow would take",
                "sqlflow dimensions table <flow.yaml> --dimension <name> [--partition <id>] [--search <text>]",
                "                         [--attr <attribute>=<value> ...] [--order value|key|records|id|<column>] [--desc] [--max <n>]",
                "                                   The dimension as one table, osdu.dim_<dimension>: a row per key and",
                "                                   value it collects, the key and its value in columns named after what",
                "                                   the dimension reads, a column per attribute, each row with the number",
                "                                   a table of facts joins on (needs --db)",
                "sqlflow dimensions values <flow.yaml> --dimension <name> [--partition <id>] [--search <text>]",
                "                          [--attr <attribute>=<value> ...] [--order value|records] [--removed] [--max <n>]",
                "                                   A dimension's values, each human-friendly value with its records, its",
                "                                   search filter and the keys most records hold; --removed adds the ones",
                "                                   no build finds any more (needs --db)",
                "sqlflow dimensions keys <flow.yaml> --dimension <name> [--partition <id>] [--value <value> | --left-out]",
                "                        [--search <text>] [--attr <attribute>=<value> ...] [--order arrival|count] [--removed] [--max <n>]",
                "                                   The keys exactly as the index holds them (an id for a reference), each",
                "                                   with its label, its value or why it has none, and its search filter;",
                "                                   --removed adds the ones no build finds any more (needs --db)",
                "sqlflow dimensions attributes <flow.yaml> --dimension <name> --attribute <name> [--partition <id>]",
                "                              [--attr <attribute>=<value> ...] [--value <value> ...] [--search <text>] [--max <n>]",
                "                                   The values an attribute of the dimension's keys holds, the most records",
                "                                   first, each with its keys; --attr and --value narrow it to the keys the",
                "                                   other picks of a cascade leave (needs --db)",
                "sqlflow dimensions filter <flow.yaml> --dimension <name> --value <value> [--value <value> ...] [--partition <id>]",
                "                                   The search that finds every record holding the values named: one query",
                "                                   a line on the console, ready to send, what it leaves out on the error",
                "                                   stream (needs --db)",
                "sqlflow dimensions search <flow.yaml> [--partition <id>] [--pick <dimension>=<value> ...]",
                "                          [--where <dimension>.<attribute>=<value> ...] [--kind <kind>] [--within <query>]",
                "                                   The search across dimensions: records holding a value picked in each",
                "                                   dimension (OR within one, AND across them), the query on the console,",
                "                                   the request to send with --json (needs --db)",
                "sqlflow dimensions history <flow.yaml> --dimension <name> [--partition <id>] [--max <n>]",
                "                                   The builds, newest first: what each found, read and changed (needs --db)",
                "sqlflow dimensions changes <flow.yaml> --dimension <name> [--partition <id>] [--build <n>] [--value <value>]",
                "                           [--change added|removed|moved|restored] [--max <n>]",
                "                                   The change log, newest first: keys that arrived, left, came back or",
                "                                   moved to another value (needs --db)",
                "sqlflow dimensions remove <flow.yaml> --dimension <name> [--partition <id>]",
                "                                   Removes a dimension the flow no longer declares, and everything kept of",
                "                                   it in the partition, for good: refused for one the flow declares or a",
                "                                   cache flow captures; recorded as an activity of the flow (needs --db)",
                "sqlflow dimensions views <flow.yaml> [--view <name> | --suggest <dimension>] [--partition <id>]",
                "                                   The flow's views, osdu.v_dim_<view>: each with its from dimension, its",
                "                                   joins, whether a build wrote it as the flow declares it, and its last",
                "                                   check; --view shows one with its columns, its checks and its SQL;",
                "                                   --suggest offers the joins a view of that dimension could make, from",
                "                                   the flow's template folders, as YAML to keep or change (needs --db)",
                "sqlflow dimensions remove-view <flow.yaml> --view <name> [--partition <id>]",
                "                                   Removes a view of the flow it no longer declares, for good: the view",
                "                                   from the database and its record with its checks; refused for one the",
                "                                   flow declares; recorded as an activity of the flow (needs --db)",
                "sqlflow dimensions export <flow.yaml> --dimension <name> [--partition <id>] [--set values|keys|table]",
                "                          [--format csv|jsonl] [--out <file>]",
                "                                   The whole of a dimension's values or keys, each with its search filter,",
                "                                   or its table: its rows and columns as the database holds them",
                "                                   (needs --db). Building is a run:",
                "                                   sqlflow run <flow.yaml> --payload '{\"dimensions\":[\"name\"]}'",
            ],
            DeliveryDimensionVerbs.DimensionsAsync)
        {
            Subcommands = ["list", "table", "values", "keys", "attributes", "filter", "search", "history", "changes", "export", "remove", "views", "remove-view"],
            ValueOptions = ["--partition", "--dimension", "--view", "--suggest", "--search", "--order", "--value", "--pick", "--where", "--attr", "--attribute", "--kind", "--within", "--max", "--build", "--change", "--set", "--format", "--out"],
            Flags = ["--removed", "--left-out", "--desc"],
        },
        new CliVerb(
            "inventory",
            [
                "sqlflow inventory list [--partition <id>]",
                "                                   The inventories of a partition (of every partition without",
                "                                   --partition): each with its flow, the kind it reads, and what its",
                "                                   last reconcile raised (needs --db)",
                "sqlflow inventory show <partition> <id>",
                "                                   One inventory: its ids by finding as its rows hold them now, the",
                "                                   owners its last reconcile used and how it knew them, and its last",
                "                                   build and reconcile (needs --db)",
                "sqlflow inventory records <partition> <id> [--finding <finding>] [--after <n>] [--limit <n>]",
                "                                   A page of its ids, of one finding or every one: what OSDU serves of",
                "                                   each, why it has its finding, and what the ledgers hold of it; the",
                "                                   line under the page names the --after of the next (needs --db)",
                "sqlflow inventory lookup <partition> <osdu-id>",
                "                                   What every inventory of the partition holds of one OSDU id (needs --db)",
                "sqlflow inventory runs <partition> <id> [--limit <n>]",
                "                                   Its builds and reconciles, newest first: what each read, changed",
                "                                   and raised (needs --db)",
                "sqlflow inventory export <partition> <id> [--finding <finding>] [--out <file.csv>]",
                "                                   Its ids, of one finding or every one, as CSV (needs --db). Building",
                "                                   is a run: sqlflow run <flow.yaml> --payload '{\"inventories\":[\"name\"]}'",
                "sqlflow inventory removals <partition> <id> [--limit <n>]",
                "                                   The removals asked of it, newest first: what each removed, soft",
                "                                   deleted or purged, what it came to, and who asked (needs --db)",
                "sqlflow inventory removal <partition> <removal> [--outcome <outcome>] [--after <n>] [--limit <n>]",
                "                                   What one removal did to each id: removed, gone, skipped or failed,",
                "                                   and why (needs --db). A removal is a run of a flow that declares",
                "                                   removal: sqlflow run <flow.yaml> --operation remove --payload @removal.json",
            ],
            DeliveryInventoryVerbs.InventoryAsync)
        {
            Subcommands = ["list", "show", "records", "lookup", "runs", "export", "removals", "removal"],
            ValueOptions = ["--partition", "--finding", "--after", "--limit", "--out", "--outcome"],
        },
        new CliVerb(
            "removal",
            [
                "sqlflow removal runs <removal.yaml> [--partition <id>] [--removal <name>] [--max <n>]",
                "                                   A removal flow's runs in a partition, newest first: what each read",
                "                                   from its sources, found removed, held for approval and took out of",
                "                                   OSDU, and who ran it (needs --db)",
                "sqlflow removal run <partition> <number>",
                "                                   One run with every count it kept (needs --db). A removal is a run:",
                "                                   sqlflow run <removal.yaml> --set partition=<id>; the delivery flow's",
                "                                   deletions are 'sqlflow records removed <flow.yaml>'",
            ],
            DeliveryRemovalVerbs.RemovalAsync)
        {
            Subcommands = ["runs", "run"],
            ValueOptions = ["--partition", "--removal", "--max"],
        },
        new CliVerb("dev", DeliveryDevVerbs.Usage, DeliveryDevVerbs.DevAsync)
        {
            Subcommands = ["setup", "check", "env"],
            ValueOptions = [.. Setup.DevCommand.ValueOptions],
            Flags = [.. Setup.DevCommand.Flags],
            // It sets the env file up and checks it, so it reads the file itself: one the CLI would refuse still reaches it.
            ReadsLocalEnvFile = true,
        },
    ];

    public void ConfigureServices(CliModuleServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The module's database is the catalog's: a command opens it on the catalog the command line names, and a node,
        // which SQLFlow gives no catalog connection, through the catalog's environment variable.
        services.AddDatabase(services.Scope == CliServiceScope.Worker ? OsduModuleDatabase.CreateForNode() : OsduModuleDatabase.Create());

        services.Services.AddDeliveryKind();
        services.Services.AddDeliveryLedger();
        services.Services.AddScoped(provider => provider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OsduDbContext>>().CreateDbContext());

        // A node runs for as long as the pod does, so its metrics are worth exporting; a one-shot command ends before
        // the first export and exports nothing by design. The node takes these settings from its environment, as it
        // takes every other one. The export is a hosted service, registered as the control plane registers it: the node
        // starts it before it takes work and stops it after it drains, and its services' disposal flushes what is left.
        if (services.Scope == CliServiceScope.Worker)
        {
            services.Services.AddDeliveryMetricsExport(TelemetryOptions.FromEnvironment(Environment.GetEnvironmentVariable));

            // The runs a node takes render against the current version of their partition's cache, which it reads into
            // memory ahead of them, so the first run after the node starts, or after a refresh, does not wait for the read.
            services.Services.AddDeliveryCacheWarmup(Catalog.CacheWarmupOptions.FromEnvironment(Environment.GetEnvironmentVariable));
        }
    }
}
